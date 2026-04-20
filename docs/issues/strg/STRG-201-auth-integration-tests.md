---
id: STRG-201
title: Authentication integration tests
milestone: v0.1
priority: high
status: open
type: testing
labels: [testing, auth]
depends_on: [STRG-200, STRG-015, STRG-016, STRG-083]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-201: Authentication integration tests

## Summary

Write integration tests for all authentication flows: password grant, token refresh, token revocation, registration, and brute-force lockout. Tests run against the full ASP.NET Core pipeline using `StrgWebApplicationFactory`.

## Technical Specification

### Test file: `tests/Strg.Integration.Tests/Auth/AuthTests.cs`

```csharp
public class AuthTests : IClassFixture<DatabaseFixture>
{
    private readonly HttpClient _client;

    public AuthTests(DatabaseFixture fixture)
    {
        _client = fixture.Factory.CreateClient();
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsJwt()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent([
            new("grant_type", "password"),
            new("username", "admin@test.com"),
            new("password", "Admin123!"),
            new("client_id", "strg-api"),
            new("scope", "files.read files.write")
        ]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("token_type").GetString().Should().Be("Bearer");
        json.GetProperty("expires_in").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns400()
    { ... }

    [Fact]
    public async Task Login_5FailedAttempts_AccountLocked()
    {
        for (int i = 0; i < 5; i++)
        {
            await _client.PostAsync("/connect/token", /* wrong password */);
        }
        var response = await _client.PostAsync("/connect/token", /* correct password */);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("locked");
    }

    [Fact]
    public async Task TokenRefresh_ValidRefreshToken_ReturnsNewAccessToken() { ... }

    [Fact]
    public async Task ApiCall_WithoutToken_Returns401() { ... }

    [Fact]
    public async Task ApiCall_WithValidToken_Returns200() { ... }
}
```

### Scenarios to cover:

1. Password grant — success
2. Password grant — wrong password
3. Password grant — unknown user (same error as wrong password)
4. 5 failed attempts → account locked
5. Token refresh — success
6. Token refresh — expired refresh token
7. Token revocation — revoked token rejected
8. API call without token → 401
9. API call with valid token → 200
10. API call with expired token → 401

## Acceptance Criteria

- [ ] All 10 scenarios covered by tests
- [ ] Tests use `FluentAssertions` for readable assertions
- [ ] Tests do not depend on test order (each test is independent)
- [ ] Lockout test creates a fresh user (not the shared admin account)

## Test Cases (see above)

## Implementation Tasks

- [ ] Create `AuthTests.cs`
- [ ] Install `FluentAssertions` package in test project
- [ ] Create helper `CreateUniqueUserAsync()` for tests that modify user state
- [ ] Ensure each test using lockout creates its own user

## Definition of Done

- [ ] All 10 scenarios pass
- [ ] No test order dependency
