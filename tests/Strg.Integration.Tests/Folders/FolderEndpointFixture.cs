using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.Folders;

/// <summary>
/// HTTP-level fixture for the STRG-042 folder-creation endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape (one container
/// per test class) and its seeded drive; adds direct <see cref="FileItem"/>-seeding so the
/// 409-on-file-collision test can pre-seed a non-directory row at a specific path without
/// running an upload, plus a count-by-driveId helper for the idempotency assertion.
///
/// <para>Paths are seeded in storage-normalized form — no leading or trailing slash, mirroring
/// what <c>StoragePath.Normalize</c> produces in production and what the existing fixtures
/// (FileListFixture, FileDeleteFixture) seed. Aligning the seed shape with production keeps
/// any path-format regression in the listing/deletion code observable here too.</para>
/// </summary>
public sealed class FolderEndpointFixture : StrgTusUploadFixture
{
    /// <summary>
    /// Seeds a single <see cref="FileItem"/> on the fixture's drive. Used by the 409 test to
    /// pre-seed a non-directory row at a path that the endpoint will later try to walk into;
    /// without the pre-seed, the endpoint would simply auto-create a directory at that path.
    /// </summary>
    public async Task<Guid> SeedFileAsync(
        string path,
        bool isDirectory = false,
        string? mimeType = null,
        long size = 0,
        CancellationToken cancellationToken = default)
    {
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

        await using var ctx = NewDbContext();
        var file = new FileItem
        {
            TenantId = TenantId,
            DriveId = DriveId,
            Name = name,
            Path = path,
            IsDirectory = isDirectory,
            Size = size,
            MimeType = mimeType ?? (isDirectory ? "inode/directory" : "application/octet-stream"),
            CreatedBy = UserId,
            VersionCount = isDirectory ? 0 : 1,
        };
        ctx.Files.Add(file);
        await ctx.SaveChangesAsync(cancellationToken);
        return file.Id;
    }

    /// <summary>
    /// Counts <see cref="FileItem"/> rows on the fixture's drive whose path starts with the
    /// given prefix. Used by the idempotency test (TC-002) to assert that a re-create call
    /// did NOT duplicate any directory rows. The prefix must be storage-normalized to match
    /// the canonical path shape.
    /// </summary>
    public async Task<int> CountByPathPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Files
            .Where(f => f.DriveId == DriveId && (f.Path == prefix || f.Path.StartsWith(prefix + "/")))
            .CountAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a single <see cref="FileItem"/> by its (drive-scoped) path. The fixture's drive is
    /// the only drive seeded in this class, so an unambiguous lookup by path is sufficient.
    /// Returns null for paths the endpoint did not (or refused to) materialise.
    /// </summary>
    public async Task<FileItem?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Files
            .FirstOrDefaultAsync(f => f.DriveId == DriveId && f.Path == path, cancellationToken);
    }

    /// <summary>
    /// POSTs the password grant with a caller-supplied scope list. Mirrors the equivalent
    /// helper in FileListFixture / FileDeleteFixture — needed to exercise the 403 path for
    /// callers authenticated without <c>files.write</c>.
    /// </summary>
    public async Task<string> AuthenticateWithScopesAsync(string scopes)
    {
        using var client = CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = UserEmail,
            ["password"] = TestPassword,
            ["client_id"] = "strg-default",
            ["scope"] = scopes,
        });
        using var response = await client.PostAsync("/connect/token", form);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }
}
