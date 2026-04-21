using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.Auth;

/// <summary>
/// STRG-083: covers that the account-lockout state machine (already pinned at the
/// <c>UserManager</c> unit-test level) is actually consulted by the <c>/connect/token</c>
/// password-grant endpoint. If the wire handler ever called <c>CheckPassword</c> directly
/// without going through <c>RecordFailedLoginAsync</c> / <c>IsLocked</c>, a locked account
/// could still authenticate — which is what these tests guard against.
/// </summary>
public sealed class BruteForceLockoutTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    // Core wire-level guarantee: once the soft-lock threshold is crossed, even a valid
    // password is rejected. This is the single most important assertion in this file — it
    // proves the lock is enforced inside the token endpoint and not just somewhere adjacent.
    [Fact]
    public async Task Correct_password_after_five_wrong_attempts_returns_invalid_grant()
    {
        await factory.ResetAdminLockoutAsync();
        await factory.ForceLockoutAttemptsAsync(StrgWebApplicationFactory.AdminEmail, 5);

        using var response = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a locked account must not authenticate even with the correct password");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    // Observability guarantee: the endpoint must actually persist the failed-attempt counter,
    // not just compute it in memory and drop it. If this assertion ever breaks, the lockout
    // would never trigger across process restarts — a silent regression that's invisible to
    // any purely in-memory test.
    [Fact]
    public async Task Five_wrong_attempts_persist_counter_and_lockout_timestamp_to_db()
    {
        await factory.ResetAdminLockoutAsync();

        await factory.ForceLockoutAttemptsAsync(StrgWebApplicationFactory.AdminEmail, 5);

        var admin = await factory.ReloadAdminAsync();
        admin.FailedLoginAttempts.Should().BeGreaterThanOrEqualTo(5,
            "every rejected /connect/token call must increment the DB counter");
        admin.LockedUntil.Should().NotBeNull(
            "crossing the soft threshold must set LockedUntil so the lock survives restart");
        admin.IsLocked.Should().BeTrue();
    }

    // TC-005 from the spec ("10 failed logins → locked for 1 hour"). Made reachable on the wire
    // by `c988a36` — ValidateCredentialsAsync now calls ApplyFailedLoginAsync even when the
    // account is already locked, so cumulative attack pressure during the short-tier lockout
    // escalates to the long tier instead of being pinned at 5. Before that fix this test would
    // have asserted a ~15min window (short tier only); the ~1h assertion is the regression guard
    // against accidentally re-introducing the short-circuit.
    [Fact]
    public async Task Ten_wrong_attempts_escalate_to_one_hour_lockout()
    {
        await factory.ResetAdminLockoutAsync();

        await factory.ForceLockoutAttemptsAsync(StrgWebApplicationFactory.AdminEmail, 10);

        var admin = await factory.ReloadAdminAsync();
        admin.FailedLoginAttempts.Should().BeGreaterThanOrEqualTo(10,
            "the locked-branch must keep incrementing so the long tier is reachable via the wire");
        admin.LockedUntil.Should().NotBeNull();
        admin.LockedUntil!.Value.Should().BeCloseTo(
            DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(2),
            "the 10-attempt threshold sets LockedUntil to now+1h (the short-tier 15min window would be a regression)");
    }
}
