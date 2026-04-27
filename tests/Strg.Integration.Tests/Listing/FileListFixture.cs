using System.Net.Http.Json;
using System.Text.Json;
using Strg.Core.Domain;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.Listing;

/// <summary>
/// HTTP-level fixture for the STRG-038 file-list endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape and its seeded
/// drive; adds a direct <c>FileItem</c>-seeding helper that bypasses the TUS pipeline so
/// listing tests get precise control over path strings, mime types, and directory flags
/// without the cost (and constraints) of running an upload per file.
///
/// <para>Paths are seeded in the production format — no leading or trailing slash — matching
/// what <c>StoragePath.Normalize</c> produces in <c>StrgTusStore</c> and
/// <c>CreateFolderHandler</c>. Existing fixtures elsewhere (<c>FileDownloadFixture</c>) seed
/// with a leading slash; that convention bypasses <c>StoragePath</c> and is inconsistent with
/// production, which would mask any path-format bug in the listing filter. We use the
/// production shape here so the tests pin the same data the endpoint will see in deployment.</para>
/// </summary>
public sealed class FileListFixture : StrgTusUploadFixture
{
    /// <summary>
    /// Seeds a single <see cref="FileItem"/> on the fixture's drive. <paramref name="path"/> must
    /// be in storage-normalized form (no leading or trailing slash, e.g. <c>"report.pdf"</c> or
    /// <c>"docs/sub/notes.txt"</c>). The directory flag is wired through to
    /// <see cref="FileItem.IsDirectory"/> so tests can assert directories-first ordering.
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
    /// Soft-deletes a previously seeded file by setting <see cref="TenantedEntity.DeletedAt"/>.
    /// Mirrors the production soft-delete contract (see
    /// <c>FileRepository.SoftDeleteAsync</c>); the global query filter on
    /// <c>StrgDbContext.Files</c> then hides the row from every subsequent query.
    /// </summary>
    public async Task SoftDeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        var file = await ctx.Files.FindAsync([fileId], cancellationToken)
            ?? throw new InvalidOperationException($"FileItem {fileId} not seeded.");
        file.DeletedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// POSTs the password grant with a caller-supplied scope list. Mirrors
    /// <c>FileDownloadFixture.AuthenticateWithScopesAsync</c> — needed to exercise the 403 path
    /// for callers without <c>files.read</c>.
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
