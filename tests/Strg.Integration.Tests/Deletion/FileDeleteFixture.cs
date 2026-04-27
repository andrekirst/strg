using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.Deletion;

/// <summary>
/// HTTP-level fixture for the STRG-039 file-delete endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape (one
/// container per test class) and its seeded encrypted drive; adds direct
/// <see cref="FileItem"/>-seeding so delete tests can construct precise directory trees
/// without running uploads, plus a <see cref="ReadFileBypassingFiltersAsync"/> helper for
/// the "row exists with DeletedAt set" post-delete assertion (the soft-delete global filter
/// would otherwise hide the row and the assertion would read as "row missing").
///
/// <para>Paths are seeded in storage-normalized form — no leading slash, no trailing slash,
/// e.g. <c>"docs"</c> for a directory and <c>"docs/sub/notes.txt"</c> for a nested file.
/// Mirrors what <c>StoragePath.Normalize</c> produces in <c>StrgTusStore</c> and the
/// existing <c>FileListFixture</c>, so production-shaped paths drive the recursive prefix
/// logic.</para>
/// </summary>
public sealed class FileDeleteFixture : StrgTusUploadFixture
{
    /// <summary>
    /// Seeds a single <see cref="FileItem"/> on the fixture's drive. <paramref name="path"/>
    /// must be in storage-normalized form (no leading or trailing slash). Set
    /// <paramref name="isDirectory"/> to <see langword="true"/> for folder entries — the
    /// recursive-delete tests rely on the directory flag to drive the descent.
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
    /// Reads a <see cref="FileItem"/> directly from the DB with global query filters
    /// disabled. The soft-delete filter would otherwise hide deleted rows and the
    /// "row exists with DeletedAt set" assertion is the whole point of the deletion
    /// tests. Returns <see langword="null"/> for genuinely-absent ids.
    /// </summary>
    public async Task<FileItem?> ReadFileBypassingFiltersAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Files
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
    }

    /// <summary>
    /// POSTs the password grant with a caller-supplied scope list. Mirrors
    /// <c>FileListFixture.AuthenticateWithScopesAsync</c> — needed to exercise the 403 path
    /// for callers without <c>files.write</c>.
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
