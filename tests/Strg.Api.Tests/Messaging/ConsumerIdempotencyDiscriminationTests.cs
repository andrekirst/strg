using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Strg.Infrastructure.Data.Configurations;
using Strg.Infrastructure.Messaging.Consumers;
using Xunit;

namespace Strg.Api.Tests.Messaging;

/// <summary>
/// STRG-062 INFO-1 (task #114) — pins the <i>negative case</i> of the EventId-unique-violation
/// discriminator on both consumers that persist idempotency-keyed rows. The positive case
/// ("a real EventId collision is swallowed as at-least-once redelivery") is already covered by
/// integration tests — a MassTransit test harness publishing the same event twice lands two
/// 23505 violations on <c>IX_AuditEntries_EventId</c> / <c>IX_Notifications_EventId</c> and the
/// catch filter swallows both. What those tests cannot cover, because <see cref="AuditEntry"/>
/// and <see cref="Strg.Core.Domain.Notification"/> each have exactly one unique index today, is
/// the discrimination shape: <c>ConstraintName == IX_AuditEntries_EventId</c> vs
/// <c>ConstraintName.Contains("EventId")</c> vs <c>just SqlState == "23505"</c> — all three
/// predicates pass every existing test.
///
/// <para><b>Regression shape defended.</b> A future refactor that broadens the predicate to any
/// of —
/// <list type="bullet">
///   <item><description>dropping the <c>ConstraintName ==</c> clause ("simplify: every 23505 in
///     this consumer is our idempotency collision anyway"),</description></item>
///   <item><description>relaxing to <c>ConstraintName?.Contains("EventId")</c>,</description></item>
///   <item><description>renaming the target index without updating the literal (though
///     <c>AuditEntryConstraintNames</c> / <c>NotificationConstraintNames</c> + MigrationTests
///     triangulation already defend that vector separately).</description></item>
/// </list>
/// — would silently swallow unique-constraint violations on future indexes added to
/// <c>AuditEntries</c> / <c>Notifications</c> (e.g. a <c>(TenantId, IdempotencyKey)</c> second
/// index for a different dedup path). The error shape is "consumer writes a row, the row
/// collides with a real business-rule constraint the row is <i>supposed</i> to violate, the
/// consumer swallows it as if it were redelivery, and the domain event is silently dropped".</para>
///
/// <para><b>Why unit-test the predicate rather than drive it through the consumer.</b> The
/// catch-filter <c>when (IsEventIdUniqueViolation(ex))</c> is a one-line gate whose entire job
/// is to delegate the decision to the predicate. If the predicate returns the right value, the
/// filter behaves correctly. Building a full <see cref="DbUpdateException"/> + interceptor
/// pipeline to prove "unrelated-constraint 23505 propagates out of Consume" pays the cost of
/// constructing a real EF change-tracker scope to pin a delegation the C# language already
/// guarantees (catch-when only executes the catch body when the predicate returns true).
/// Promoting the predicate to <c>internal static</c> (via <c>InternalsVisibleTo</c>) and
/// testing it directly is the cheapest path that actually pins the discrimination intent.</para>
///
/// <para><b>Why fabricate PostgresException rather than manufacture a real collision.</b>
/// Manufacturing a second 23505 with a non-EventId <c>ConstraintName</c> would require adding a
/// second unique index to <see cref="AuditEntry"/> or <see cref="Strg.Core.Domain.Notification"/>
/// that doesn't yet exist in the schema, running a real INSERT that violates it, and binding
/// the result through EF's <c>DbUpdateException</c> wrapper. Every one of those steps is
/// domain-extraneous to "does the predicate discriminate on ConstraintName correctly".
/// <see cref="PostgresException"/> ships a public constructor taking
/// <c>messageText, severity, invariantSeverity, sqlState, …, constraintName</c> precisely so
/// Npgsql-aware code can be unit-tested without a live connection — the fabrication path is
/// the library author's intended shape.</para>
/// </summary>
public sealed class ConsumerIdempotencyDiscriminationTests
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string CheckViolation = "23514";

    // ─── AuditLogConsumer.IsEventIdUniqueViolation ─────────────────────────────────────

    [Fact]
    public void Audit_swallows_23505_on_EventId_unique_index()
    {
        var ex = BuildDbUpdateException(UniqueViolation, AuditEntryConstraintNames.EventIdUniqueIndex);

        AuditLogConsumer.IsEventIdUniqueViolation(ex).Should().BeTrue(
            "the positive case — real redelivery collision on the EventId index — is the only " +
            "shape the catch filter is licensed to swallow");
    }

    [Fact]
    public void Audit_rethrows_23505_on_different_unique_index()
    {
        // The canonical drift shape: a future (TenantId, IdempotencyKey) second unique index
        // lands on AuditEntries, and a genuine business-rule violation on THAT index fires a
        // 23505 with a DIFFERENT ConstraintName. The predicate must return false here so the
        // catch filter lets the exception escape Consume and MassTransit retries or dead-letters
        // the event. Swallowing this case would silently drop the event.
        var ex = BuildDbUpdateException(UniqueViolation, "IX_AuditEntries_TenantId_IdempotencyKey");

        AuditLogConsumer.IsEventIdUniqueViolation(ex).Should().BeFalse(
            "any 23505 whose ConstraintName is not the EventId unique index must propagate — " +
            "broadening to `SqlState == 23505` alone would silently swallow real collisions on " +
            "any future unique index added to the AuditEntries table");
    }

    [Fact]
    public void Audit_rethrows_23505_on_substring_match_of_EventId()
    {
        // Substring-match hazard: a previous (pre-STRG-062-INFO-2) implementation used
        // ConstraintName.Contains("EventId"), which would have silently bucketed any unrelated
        // unique index whose name happens to include "EventId" (e.g. a hypothetical
        // IX_AuditEntries_EventIdMapping) into the "already-persisted" case. Equality-match
        // refuses that hazard. This test fabricates the substring-but-not-equal shape to prove
        // the equality check is doing the discriminating work.
        var ex = BuildDbUpdateException(UniqueViolation, "IX_AuditEntries_EventIdMapping");

        AuditLogConsumer.IsEventIdUniqueViolation(ex).Should().BeFalse(
            "equality-match (not Contains) on ConstraintName is what defends against " +
            "substring-drift — any refactor that reverts to Contains would fail this assertion");
    }

    [Fact]
    public void Audit_rethrows_non_23505_sqlstate_even_on_EventId_index_name()
    {
        // Defence-in-depth: even if ConstraintName somehow carries the EventId index name,
        // a non-23505 SqlState (e.g. 23503 foreign-key or 23514 check violation) is never
        // redelivery — the row didn't land, it was rejected for a different reason. Both
        // clauses of the AND must hold.
        var ex = BuildDbUpdateException(ForeignKeyViolation, AuditEntryConstraintNames.EventIdUniqueIndex);

        AuditLogConsumer.IsEventIdUniqueViolation(ex).Should().BeFalse(
            "SqlState gate must also refuse non-23505 states — a 23503/23514 on the EventId " +
            "index name is still not redelivery");
    }

    [Fact]
    public void Audit_rethrows_when_InnerException_is_not_PostgresException()
    {
        // The ORM can wrap non-Npgsql causes inside DbUpdateException — connection teardown,
        // cancellation, interceptor failures. None of those are idempotency collisions.
        var ex = new DbUpdateException(
            "non-Postgres cause",
            new InvalidOperationException("some other failure mode"));

        AuditLogConsumer.IsEventIdUniqueViolation(ex).Should().BeFalse(
            "predicate must refuse any DbUpdateException whose InnerException isn't a PostgresException");
    }

    // ─── QuotaNotificationConsumer.IsDuplicateEventId ──────────────────────────────────

    [Fact]
    public void QuotaNotification_swallows_23505_on_EventId_unique_index()
    {
        var ex = BuildDbUpdateException(UniqueViolation, NotificationConstraintNames.EventIdUniqueIndex);

        QuotaNotificationConsumer.IsDuplicateEventId(ex).Should().BeTrue(
            "the positive case: a real redelivery collision on IX_Notifications_EventId must be " +
            "swallowed as at-least-once redelivery");
    }

    [Fact]
    public void QuotaNotification_rethrows_23505_on_different_unique_index()
    {
        // Symmetric to the AuditLogConsumer case — a future (TenantId, UserId, Level)
        // dedup constraint on Notifications would fire 23505 with a different ConstraintName,
        // and mis-classifying it as redelivery silently drops the warning.
        var ex = BuildDbUpdateException(UniqueViolation, "IX_Notifications_Tenant_User_Level");

        QuotaNotificationConsumer.IsDuplicateEventId(ex).Should().BeFalse(
            "QuotaNotificationConsumer's predicate mirrors AuditLogConsumer's — non-EventId " +
            "23505 must propagate so the consumer can retry or dead-letter");
    }

    [Fact]
    public void QuotaNotification_rethrows_23505_on_substring_match_of_EventId()
    {
        var ex = BuildDbUpdateException(UniqueViolation, "IX_Notifications_EventIdIndex");

        QuotaNotificationConsumer.IsDuplicateEventId(ex).Should().BeFalse(
            "equality-match defends against substring-drift on the Notifications side as well");
    }

    [Fact]
    public void QuotaNotification_rethrows_non_23505_sqlstate_even_on_EventId_index_name()
    {
        var ex = BuildDbUpdateException(CheckViolation, NotificationConstraintNames.EventIdUniqueIndex);

        QuotaNotificationConsumer.IsDuplicateEventId(ex).Should().BeFalse(
            "23514 (check violation) on the EventId index name is still not redelivery");
    }

    [Fact]
    public void QuotaNotification_rethrows_when_InnerException_is_not_PostgresException()
    {
        var ex = new DbUpdateException(
            "non-Postgres cause",
            new TimeoutException("connection pool exhausted"));

        QuotaNotificationConsumer.IsDuplicateEventId(ex).Should().BeFalse(
            "non-PostgresException inner causes are unrelated to Npgsql unique-constraint collisions");
    }

    // ─── Fabrication helper ────────────────────────────────────────────────────────────

    // PostgresException's public (string, string, string, string, …, constraintName, …)
    // constructor is the Npgsql-author-sanctioned fabrication path for tests that want to drive
    // Npgsql-aware error-handling code without a live connection. See
    // https://www.npgsql.org/doc/api/Npgsql.PostgresException.html
    private static DbUpdateException BuildDbUpdateException(string sqlState, string? constraintName) =>
        new(
            "test-only synthetic DbUpdateException",
            new PostgresException(
                messageText: "fabricated unique-violation for predicate test",
                severity: "ERROR",
                invariantSeverity: "ERROR",
                sqlState: sqlState,
                constraintName: constraintName));
}
