---
id: STRG-202
title: File operations integration tests
milestone: v0.1
priority: high
status: open
type: testing
labels: [testing, files]
depends_on: [STRG-200, STRG-034, STRG-037, STRG-038, STRG-039, STRG-040, STRG-041, STRG-042]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-202: File operations integration tests

## Summary

Write integration tests for the full file operation lifecycle: upload via TUS, download, list, delete, move, copy, and folder creation. Uses `StrgWebApplicationFactory` with in-memory storage.

## Technical Specification

### TUS upload helper:

```csharp
public static class TusTestHelper
{
    public static async Task<string> UploadAsync(
        HttpClient client,
        byte[] content,
        string fileName,
        string driveId)
    {
        // 1. POST to create upload
        var createResponse = await client.PostAsync(
            $"/api/v1/tus",
            null,
            headers: new()
            {
                ["Upload-Length"] = content.Length.ToString(),
                ["Upload-Metadata"] = $"filename {Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName))}, driveId {Convert.ToBase64String(Encoding.UTF8.GetBytes(driveId))}"
            });
        var uploadUrl = createResponse.Headers.Location!;

        // 2. PATCH to upload data
        await client.PatchAsync(uploadUrl, content);

        return uploadUrl.ToString(); // returns file ID
    }
}
```

### Test class: `tests/Strg.Integration.Tests/Files/FileOperationsTests.cs`

```csharp
public class FileOperationsTests : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Upload_SmallFile_FileItemCreatedInDatabase() { ... }

    [Fact]
    public async Task Download_UploadedFile_ReturnsSameBytes() { ... }

    [Fact]
    public async Task ListFiles_AfterUpload_FileAppearsInListing() { ... }

    [Fact]
    public async Task DeleteFile_SoftDeletesItem() { ... }

    [Fact]
    public async Task MoveFile_UpdatesPath() { ... }

    [Fact]
    public async Task CopyFile_CreatesNewItemWithDifferentId() { ... }

    [Fact]
    public async Task CreateFolder_ParentAutoCreated() { ... }

    [Fact]
    public async Task UploadExceedingQuota_Returns507() { ... }

    [Fact]
    public async Task DownloadWithRangeHeader_Returns206() { ... }
}
```

## Acceptance Criteria

- [ ] Upload → download round trip produces identical bytes
- [ ] All CRUD operations tested
- [ ] Quota enforcement tested (507 response)
- [ ] Range download tested (206 response)
- [ ] Folder auto-creation tested
- [ ] Outbox event published for each operation (verified via MassTransit test harness)

## Test Cases

- **TC-001**: Upload 1KB file → `FileItem` in DB with correct size/hash
- **TC-002**: Download → bytes match upload
- **TC-003**: Delete → file absent from listing
- **TC-004**: Move → old path 404, new path 200
- **TC-005**: Copy → new `Id`, same content
- **TC-006**: Create folder `a/b/c` → `a`, `a/b`, `a/b/c` all exist
- **TC-007**: Upload exceeding 100MB quota → 507
- **TC-008**: `FileUploadedEvent` message in MassTransit harness after upload

## Implementation Tasks

- [ ] Create `TusTestHelper.cs`
- [ ] Create `FileOperationsTests.cs` with all 9 test methods
- [ ] Set quota to 100MB in `DatabaseFixture` seed for quota test
- [ ] Verify MassTransit harness receives correct messages

## Definition of Done

- [ ] All tests pass
- [ ] Round-trip upload/download verified
