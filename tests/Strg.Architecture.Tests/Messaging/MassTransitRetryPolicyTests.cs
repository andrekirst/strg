using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Strg.Architecture.Tests.Messaging;

/// <summary>
/// STRG-062 INFO-1 (task #84) — pins that <c>MassTransitExtensions.AddStrgMassTransit</c> ships
/// with a non-trivial retry policy on the RabbitMQ bus factory. The production shape (per the
/// STRG-061 spec) is
/// <c>cfg.UseMessageRetry(r =&gt; r.Exponential(retryLimit: 5, …))</c>; retries run the consumer
/// pipeline multiple times before MassTransit publishes <c>Fault&lt;T&gt;</c> to the per-consumer
/// dead-letter exchange. Remove that line and the consumer pipeline degrades to "one attempt
/// then dead-letter", silently losing the redelivery envelope the STRG-062 audit trail relies
/// on for transient DB/broker blips.
///
/// <para><b>Regression shape defended.</b> TC-003 in
/// <c>tests/Strg.Integration.Tests/Messaging/AuditLogConsumerTests.DeadLetter_log_renders_ExceptionType_and_Message_via_explicit_projection</c>
/// configures a <i>harness-local</i> retry policy
/// (<c>cfg.UseMessageRetry(r =&gt; r.Immediate(2))</c>) so the container-backed test runs fast. The
/// harness is deliberately divergent from production — Immediate(2) fires in milliseconds,
/// Exponential(5, 1s … 30s) would wall-clock the test suite for minutes. A future refactor that
/// removes or zeroes <c>cfg.UseMessageRetry(…)</c> from <c>AddStrgMassTransit</c> would leave
/// TC-003 green (its retry config is local) while production publishes every transient failure
/// straight to <c>Fault&lt;T&gt;</c>. This test is the triangulation point that catches that drift.</para>
///
/// <para><b>Why source-text rather than runtime probe.</b> MassTransit's <c>IBusControl</c> pipe
/// probe exposes the middleware graph but does not surface retry-policy <i>parameters</i> in a
/// stable, version-portable shape — the probe JSON changes between MT major versions and the
/// "is this Exponential vs Immediate?" discriminator is not a first-class probe field. Paired
/// with the fact that building <c>AddStrgMassTransit</c>'s service provider needs a live
/// Postgres + RabbitMQ just to call <c>UsingRabbitMq</c>'s configure callback, the introspection
/// path costs orders of magnitude more runtime for a weaker assertion. Mirrors
/// <see cref="OutboxWrappedPublishEndpointTests"/>: same file, same pattern, same trade-off.</para>
///
/// <para><b>Floor, not ceiling.</b> Per team-lead criterion on this task: the test must fail on
/// retry-config DROP, not just on retry-config DRIFT. The attempt-count range [3, 10] exists to
/// allow tuning (e.g. dropping to 4 for a tight DLQ budget, bumping to 8 for a pathologically
/// flaky downstream) without requiring a test rewrite, while refusing the pathological
/// "retryLimit: 0" or "retryLimit: 1" shapes that functionally equal no retry at all.</para>
/// </summary>
public sealed class MassTransitRetryPolicyTests
{
    private const string SourcePath = "src/Strg.Infrastructure/Messaging/MassTransitExtensions.cs";

    [Fact]
    public void AddStrgMassTransit_configures_UseMessageRetry_on_the_bus_factory()
    {
        var source = RepoPath.Read(SourcePath);

        source.Should().Contain(
            "cfg.UseMessageRetry(",
            because: "dropping UseMessageRetry collapses the consumer pipeline to " +
                     "one-attempt-then-dead-letter — every transient DB/broker blip becomes a " +
                     "Fault<T> immediately, and the STRG-062 redelivery-idempotency contract " +
                     "(EventId unique index collapsing duplicate audit rows on retry) loses " +
                     "its reason to exist. This test is the only triangulation point catching " +
                     "that drop because TC-003's harness uses its own local r.Immediate(2).");
    }

    [Fact]
    public void AddStrgMassTransit_uses_exponential_backoff_not_immediate_retry()
    {
        var source = RepoPath.Read(SourcePath);

        source.Should().Contain(
            "r.Exponential(",
            because: "Exponential backoff spreads retries across wall-clock time so a broker " +
                     "or DB hiccup on the downstream has a chance to self-heal before the " +
                     "envelope lands in the dead-letter queue. Immediate() fires all attempts " +
                     "in the same tens-of-milliseconds window, which both amplifies the load on " +
                     "the failing downstream and renders the retry budget cosmetic. The test " +
                     "harness uses Immediate() deliberately (speed) — production must not.");
    }

    [Fact]
    public void AddStrgMassTransit_retry_limit_is_in_reasonable_non_zero_range()
    {
        var source = RepoPath.Read(SourcePath);

        // retryLimit is a named argument in the Exponential(...) call. A non-named-argument
        // refactor (positional-only) would false-positive this matcher — accepted cost: the
        // .editorconfig in this repo prefers named args for multi-parameter numeric calls, and
        // a future style change would be visible in the diff that changes this test too.
        var match = Regex.Match(source, @"retryLimit:\s*(\d+)");
        match.Success.Should().BeTrue(
            "retryLimit: N must appear as a named argument inside the Exponential(...) call — " +
            "a positional-only refactor would hide the attempt count from this scan and require " +
            "updating this test alongside MassTransitExtensions.cs");

        var limit = int.Parse(match.Groups[1].Value);

        limit.Should().BeInRange(3, 10,
            because: "retryLimit floor of 3 refuses the degenerate 0/1/2 shapes that functionally " +
                     "equal no retry. Ceiling of 10 refuses pathological values where the " +
                     "cumulative backoff exceeds the outbox DuplicateDetectionWindow (30min) or " +
                     "starves Prometheus dead-letter-rate alerts. Tune inside [3, 10] without " +
                     "touching this test; leave the range when the ops posture genuinely shifts.");
    }
}
