using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.Folders;

/// <summary>
/// HTTP-level fixture for the STRG-042 folder-creation endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape (one container per
/// test class) and its seeded drive/user/auth wiring; adds metadata-only seeding helpers needed
/// for the file-collides-with-folder test (TC-003) and verification helpers for the per-segment
/// ParentId chain assertion (TC-001).
///
/// <para>Paths are seeded in storage-normalized form — no leading slash, no trailing slash.
/// Mirrors what <c>StoragePath.Normalize</c> produces in production.</para>
/// </summary>
public sealed class FolderCreateFixture : StrgTusUploadFixture
{
    /// <summary>
    /// Seeds a single non-directory <see cref="FileItem"/> on the fixture's drive (metadata only,
    /// no bytes). Used exclusively by TC-003 to set up the "POST under an existing FILE returns
    /// 409" path; the folder-creation handler does not touch bytes anywhere on its happy path, so
    /// byte-level seeding is unnecessary for any of the four issue test cases.
    /// </summary>
    public async Task<Guid> SeedFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

        await using var ctx = NewDbContext();
        var file = new FileItem
        {
            TenantId = TenantId,
            DriveId = DriveId,
            Name = name,
            Path = path,
            IsDirectory = false,
            Size = 0,
            MimeType = "application/octet-stream",
            CreatedBy = UserId,
            VersionCount = 1,
        };
        ctx.Files.Add(file);
        await ctx.SaveChangesAsync(cancellationToken);
        return file.Id;
    }

    /// <summary>
    /// Reads a <see cref="FileItem"/> at the given path on the fixture's drive via the standard
    /// query-filter-respecting path. Returns <see langword="null"/> when the row is missing,
    /// soft-deleted, or in a foreign tenant.
    /// </summary>
    public async Task<FileItem?> ReadFileByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Files
            .FirstOrDefaultAsync(f => f.DriveId == DriveId && f.Path == path, cancellationToken);
    }

    /// <summary>
    /// Counts <see cref="FileItem"/> rows on the fixture's drive whose path matches exactly. Used
    /// by TC-002 to assert that a second POST of the same path produced no duplicate row.
    /// </summary>
    public async Task<int> CountByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Files
            .CountAsync(f => f.DriveId == DriveId && f.Path == path, cancellationToken);
    }

    /// <summary>
    /// Issues the password grant against a caller-supplied scope list. Mirrors
    /// <c>FileMoveFixture.AuthenticateWithScopesAsync</c> / <c>FileCopyFixture.AuthenticateWithScopesAsync</c>;
    /// needed to exercise the 403 path for callers without <c>files.write</c>.
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
