---
id: STRG-074
title: WebDAV integration tests (RFC 4918 compliance)
milestone: v0.1
priority: high
status: done
type: testing
labels: [webdav, testing]
depends_on: [STRG-067, STRG-068, STRG-069, STRG-070, STRG-071, STRG-072, STRG-073]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-074: WebDAV integration tests (RFC 4918 compliance)

## Summary

Write a comprehensive integration test suite for the WebDAV server using `HttpClient` directly against the `WebApplicationFactory<Program>` test host. Tests cover all WebDAV methods and the Basic Auth → JWT bridge.

## Technical Specification

### Test project: `tests/Strg.Integration.Tests/WebDav/`

### Test base class:

```csharp
public abstract class WebDavTestBase : IClassFixture<StrgWebApplicationFactory>
{
    protected readonly HttpClient Client;
    protected readonly StrgWebApplicationFactory Factory;
    protected const string TestDrive = "test-drive";

    protected WebDavTestBase(StrgWebApplicationFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
        // Pre-authenticate with Basic Auth
        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("admin@test.com:password123")));
    }

    protected async Task CreateTestDriveAsync()
    {
        // Create drive via REST API using admin JWT
    }

    protected async Task PutFileAsync(string path, byte[] content, string contentType = "application/octet-stream")
    {
        var response = await Client.PutAsync(
            $"/dav/{TestDrive}/{path}",
            new ByteArrayContent(content) { Headers = { ContentType = new(contentType) } });
        response.EnsureSuccessStatusCode();
    }
}
```

### Test classes:

```csharp
public class PropfindTests : WebDavTestBase
{
    [Fact] public async Task Propfind_Root_ReturnsMultiStatus() { ... }
    [Fact] public async Task Propfind_DepthInfinity_OverCap_Returns507InsufficientStorage() { ... }
    [Fact] public async Task Propfind_Directory_ListsChildren() { ... }
}

public class GetPutTests : WebDavTestBase
{
    [Fact] public async Task Get_ExistingFile_ReturnsFileContent() { ... }
    [Fact] public async Task Put_NewFile_CreatesFileItem() { ... }
    [Fact] public async Task Get_WithRangeHeader_Returns206() { ... }
}

public class MkcolDeleteTests : WebDavTestBase
{
    [Fact] public async Task Mkcol_NewDirectory_Returns201() { ... }
    [Fact] public async Task Delete_File_SoftDeletesFromDatabase() { ... }
    [Fact] public async Task Delete_Directory_RecursivelyDeletesChildren() { ... }
}

public class CopyMoveTests : WebDavTestBase
{
    [Fact] public async Task Copy_File_CreatesDuplicate() { ... }
    [Fact] public async Task Move_File_UpdatesPath() { ... }
}

public class LockTests : WebDavTestBase
{
    [Fact] public async Task Lock_Resource_ReturnsLockToken() { ... }
    [Fact] public async Task Lock_Twice_Returns423() { ... }
    [Fact] public async Task Unlock_WithCorrectToken_Returns204() { ... }
}

public class AuthTests : WebDavTestBase
{
    [Fact] public async Task BasicAuth_ValidCredentials_Returns207() { ... }
    [Fact] public async Task BasicAuth_InvalidCredentials_Returns401() { ... }
    [Fact] public async Task NoAuth_Returns401WithWwwAuthenticate() { ... }
}
```

### XML assertions helper:

```csharp
public static class WebDavAssert
{
    public static void HasMultiStatus(HttpResponseMessage response)
    {
        Assert.Equal(207, (int)response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
    }

    public static async Task<XDocument> ParseMultiStatusAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return XDocument.Parse(content);
    }

    public static void ContainsHref(XDocument doc, string href)
    {
        var hrefs = doc.Descendants(XName.Get("href", "DAV:")).Select(e => e.Value);
        Assert.Contains(href, hrefs);
    }
}
```

## Acceptance Criteria

- [ ] All HTTP methods tested (OPTIONS, PROPFIND, GET, PUT, HEAD, MKCOL, DELETE, COPY, MOVE, LOCK, UNLOCK)
- [ ] `Depth: 0` and `Depth: 1` PROPFIND tested separately
- [ ] `Depth: infinity` → 507 Insufficient Storage over the configured `PropfindInfinityMaxItems` cap tested (RFC 4918 §9.7.3 is the DoS-cap status, NOT 403 — the original spec draft assumed a blanket refusal, but STRG-069's implementation bounds-checks descendants against a configurable cap and surfaces 507 only when the cap is exceeded; the pin lives in `WebDavPropFindTests.TC006_depth_infinity_over_cap_returns_507` today. 403 would be semantically wrong because it implies "you lack permission," whereas the server is fine with the verb in principle — it's the response-size budget that's the problem.)
- [ ] Range requests tested (206 Partial Content)
- [ ] Recursive soft-delete verified against DB state
- [ ] Lock/unlock lifecycle tested
- [ ] Basic Auth → JWT bridge tested (correct and wrong credentials)

## Test Cases

See individual method issues (STRG-069 through STRG-073) for per-method test cases.

- **TC-001**: OPTIONS → `DAV: 1, 2` header present
- **TC-002**: PROPFIND after PUT → file appears in listing
- **TC-003**: GET after PUT → identical bytes
- **TC-004**: DELETE then PROPFIND → file absent
- **TC-005**: Full workflow: auth → MKCOL → PUT → PROPFIND → GET → DELETE

## Implementation Tasks

- [ ] Create `WebDavTestBase.cs` with auth helpers
- [ ] Create test classes for each method group
- [ ] Create `WebDavAssert` XML assertion helper
- [ ] Configure `StrgWebApplicationFactory` to provision test drive and user
- [ ] Use `InMemoryStorageProvider` (STRG-030) as storage backend in tests

## Security Review Checklist

- [ ] Tests verify that unauthenticated requests return 401
- [ ] Tests verify path traversal attempts return 400

## Definition of Done

- [ ] All tests pass with `dotnet test`
- [ ] Tests run in CI with SQLite in-memory provider
