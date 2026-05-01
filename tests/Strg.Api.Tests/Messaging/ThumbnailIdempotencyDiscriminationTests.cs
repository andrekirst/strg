using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Strg.Infrastructure.Data.Configurations;
using Strg.Infrastructure.Thumbnails;
using Xunit;

namespace Strg.Api.Tests.Messaging;

/// <summary>
/// STRG-331 — pins the negative case of <see cref="ThumbnailService.IsThumbnailUniqueViolation"/>.
/// Mirror of <see cref="ConsumerIdempotencyDiscriminationTests"/> for the audit consumer:
/// a future refactor that broadens the predicate (drops the <c>ConstraintName</c> equality, or
/// relaxes to <c>Contains</c>) would silently swallow unrelated unique violations on future
/// indexes added to <c>ThumbnailEntries</c> (e.g. a <c>(FileId, GeneratorVersion)</c> dedup
/// index for a generator-bump regen).
/// </summary>
public sealed class ThumbnailIdempotencyDiscriminationTests
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string CheckViolation = "23514";

    [Fact]
    public void Swallows_23505_on_pinned_unique_index()
    {
        var ex = BuildDbUpdateException(UniqueViolation, ThumbnailEntryConstraintNames.UniqueIndex);

        ThumbnailService.IsThumbnailUniqueViolation(ex).Should().BeTrue(
            "the positive case — real redelivery collision on the (FileVersionId, Variant, Format) " +
            "index — is the only shape the catch filter is licensed to swallow");
    }

    [Fact]
    public void Rethrows_23505_on_different_unique_index()
    {
        // Hypothetical second unique index that's NOT the idempotency key.
        var ex = BuildDbUpdateException(UniqueViolation, "IX_ThumbnailEntries_TenantId_FileId_Variant");

        ThumbnailService.IsThumbnailUniqueViolation(ex).Should().BeFalse(
            "a 23505 on a different unique index is a real domain-rule violation that must " +
            "propagate so MassTransit retries / dead-letters; swallowing it would silently drop " +
            "the failure mode");
    }

    [Fact]
    public void Rethrows_23505_on_substring_match_to_constraint_name()
    {
        // A future index whose name happens to contain the same substring (e.g.
        // "IX_ThumbnailEntries_FileVersionId" — different shape, different intent). The exact-
        // equality contract pinned here means that name does NOT collapse into the idempotency
        // bucket.
        var ex = BuildDbUpdateException(UniqueViolation, "IX_ThumbnailEntries_FileVersionId");

        ThumbnailService.IsThumbnailUniqueViolation(ex).Should().BeFalse(
            "exact equality (not substring match) — a future index whose name shares a prefix " +
            "with the canonical idempotency index is a different constraint and must NOT be " +
            "swallowed");
    }

    [Fact]
    public void Rethrows_23503_FK_violation_on_target_index_name()
    {
        var ex = BuildDbUpdateException(ForeignKeyViolation, ThumbnailEntryConstraintNames.UniqueIndex);

        ThumbnailService.IsThumbnailUniqueViolation(ex).Should().BeFalse(
            "non-23505 SqlStates (FK violations, check violations) are unrelated failure modes " +
            "and must propagate even when the constraint name happens to match");
    }

    [Fact]
    public void Rethrows_23514_check_violation_on_target_index_name()
    {
        var ex = BuildDbUpdateException(CheckViolation, ThumbnailEntryConstraintNames.UniqueIndex);

        ThumbnailService.IsThumbnailUniqueViolation(ex).Should().BeFalse();
    }

    [Fact]
    public void Rethrows_when_InnerException_is_not_PostgresException()
    {
        var ex = new DbUpdateException(
            "non-Postgres cause",
            new TimeoutException("connection pool exhausted"));

        ThumbnailService.IsThumbnailUniqueViolation(ex).Should().BeFalse(
            "non-PostgresException inner causes (e.g. SQLite layer in dev, transient network) " +
            "are unrelated to the Npgsql unique-violation contract");
    }

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
