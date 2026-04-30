using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.GraphQl.Inputs.File;
using Strg.GraphQl.Queries;
using Strg.GraphQl.Queries.Storage;
using Strg.GraphQl.Tests.Helpers;
using Strg.GraphQl.Types;
using Strg.Infrastructure.Data;
using Xunit;
using DomainTag = Strg.Core.Domain.Tag;

namespace Strg.GraphQl.Tests.Queries;

/// <summary>
/// STRG-048 — GraphQL coverage of <c>where: { tags: { some: { ... } } }</c> via the scoped
/// <see cref="FileItemFilterInputType"/>, plus user-isolation checks driven by the
/// <c>StrgDbContext</c> "TagUser" named query filter.
///
/// <para>Each test sets <see cref="TestCurrentUser.Shared"/>'s <c>UserId</c> for the duration
/// of the test. The <c>[Collection("database")]</c> attribute serializes test execution so
/// concurrent mutations of <c>Shared</c> cannot race.</para>
/// </summary>
[Collection("database")]
public class FileTagFilterQueriesTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;
    private static readonly TestCurrentUser SharedUserCtx = TestCurrentUser.Shared;

    // TC-002 — filter shape `where: { tags: { some: { key: { eq: "project" } } } }` returns
    // every file tagged with the matching key, regardless of value.
    [Fact]
    public async Task TC002_FilterByTagKey_ReturnsMatchingFiles()
    {
        var tenantId = Guid.NewGuid();
        var driveId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        SharedUserCtx.UserId = userId;
        var dbName = Guid.NewGuid().ToString();

        var executor = await BuildExecutorAsync(dbName);

        Guid taggedReportId, taggedArchiveId, untaggedNotesId;
        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            taggedReportId = AddFile(db, tenantId, driveId, "report.pdf");
            untaggedNotesId = AddFile(db, tenantId, driveId, "notes.txt");
            taggedArchiveId = AddFile(db, tenantId, driveId, "archive.zip");
            db.Tags.Add(new DomainTag
            {
                TenantId = tenantId, FileId = taggedReportId, UserId = userId,
                Key = "project", Value = "acme",
            });
            db.Tags.Add(new DomainTag
            {
                TenantId = tenantId, FileId = taggedArchiveId, UserId = userId,
                Key = "project", Value = "globex",
            });
            await db.SaveChangesAsync();
        }

        var result = (IOperationResult)await executor.ExecuteAsync(
            $"{{ storage {{ files(driveId: \"{driveId}\", where: {{ tags: {{ some: {{ key: {{ eq: \"project\" }} }} }} }}) {{ nodes {{ id name }} totalCount }} }} }}");

        using var doc = JsonDocument.Parse(result.ToJson());
        Assert.True(doc.RootElement.TryGetProperty("data", out var data),
            $"expected data, got: {result.ToJson()}");
        var files = data.GetProperty("storage").GetProperty("files");
        Assert.Equal(2, files.GetProperty("totalCount").GetInt32());

        var names = new HashSet<string>();
        foreach (var node in files.GetProperty("nodes").EnumerateArray())
        {
            names.Add(node.GetProperty("name").GetString()!);
        }
        Assert.Contains("report.pdf", names);
        Assert.Contains("archive.zip", names);
        Assert.DoesNotContain("notes.txt", names);
    }

    // TC-001 (GraphQL half) — User A's tags do not surface to User B's query.
    [Fact]
    public async Task TC001_TagFilter_IsScopedToCurrentUser()
    {
        var tenantId = Guid.NewGuid();
        var driveId = Guid.NewGuid();
        var userAId = Guid.NewGuid();
        var userBId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        var dbName = Guid.NewGuid().ToString();

        // User A is the seeding scope. The Tag global filter would block user-B-owned tag inserts
        // through normal LINQ paths, but the seeding code adds rows directly via DbSet.Add which
        // bypasses query filters — exactly what the test needs.
        SharedUserCtx.UserId = userAId;
        var executor = await BuildExecutorAsync(dbName);

        Guid sharedFileId;
        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            sharedFileId = AddFile(db, tenantId, driveId, "shared.pdf");
            db.Tags.Add(new DomainTag
            {
                TenantId = tenantId, FileId = sharedFileId, UserId = userAId,
                Key = "project", Value = "acme",
            });
            db.Tags.Add(new DomainTag
            {
                TenantId = tenantId, FileId = sharedFileId, UserId = userBId,
                Key = "project", Value = "globex",
            });
            await db.SaveChangesAsync();
        }

        // User A: where tags.some key=project value=acme → file is hit.
        SharedUserCtx.UserId = userAId;
        var resultA = (IOperationResult)await executor.ExecuteAsync(
            $"{{ storage {{ files(driveId: \"{driveId}\", where: {{ tags: {{ some: {{ key: {{ eq: \"project\" }}, value: {{ eq: \"acme\" }} }} }} }}) {{ totalCount }} }} }}");
        Assert.Equal(1, ExtractTotalCount(resultA));

        // User B: same query for value=acme — User A's tag is filtered out by the global
        // TagUser filter, so the file does NOT match B's tag-with-acme predicate.
        SharedUserCtx.UserId = userBId;
        var resultB = (IOperationResult)await executor.ExecuteAsync(
            $"{{ storage {{ files(driveId: \"{driveId}\", where: {{ tags: {{ some: {{ key: {{ eq: \"project\" }}, value: {{ eq: \"acme\" }} }} }} }}) {{ totalCount }} }} }}");
        Assert.Equal(0, ExtractTotalCount(resultB));

        // Sanity: User B querying for their own value=globex sees the file.
        var resultBSelf = (IOperationResult)await executor.ExecuteAsync(
            $"{{ storage {{ files(driveId: \"{driveId}\", where: {{ tags: {{ some: {{ key: {{ eq: \"project\" }}, value: {{ eq: \"globex\" }} }} }} }}) {{ totalCount }} }} }}");
        Assert.Equal(1, ExtractTotalCount(resultBSelf));
    }

    // Security checklist — `FileItem.tags` field returns ONLY current user's tags. Pins the
    // resolver in FileItemType.cs against the Tag global query filter.
    [Fact]
    public async Task FileItemTags_ReturnsOnlyCurrentUserTags()
    {
        var tenantId = Guid.NewGuid();
        var driveId = Guid.NewGuid();
        var userAId = Guid.NewGuid();
        var userBId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        SharedUserCtx.UserId = userAId;
        var dbName = Guid.NewGuid().ToString();

        var executor = await BuildExecutorAsync(dbName);

        Guid sharedFileId;
        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            sharedFileId = AddFile(db, tenantId, driveId, "shared.pdf");
            db.Tags.Add(new DomainTag
            {
                TenantId = tenantId, FileId = sharedFileId, UserId = userAId,
                Key = "owner", Value = "alice",
            });
            db.Tags.Add(new DomainTag
            {
                TenantId = tenantId, FileId = sharedFileId, UserId = userBId,
                Key = "owner", Value = "bob",
            });
            await db.SaveChangesAsync();
        }

        // User A queries the file's tags field — only user A's "owner=alice" tag should come back.
        SharedUserCtx.UserId = userAId;
        var resultA = (IOperationResult)await executor.ExecuteAsync(
            $"{{ storage {{ files(driveId: \"{driveId}\") {{ nodes {{ tags {{ nodes {{ key value }} }} }} }} }} }}");
        var tagsA = ExtractTags(resultA);
        Assert.Single(tagsA);
        Assert.Equal("owner", tagsA[0].Key);
        Assert.Equal("alice", tagsA[0].Value);

        // User B queries the SAME file — only user B's "owner=bob" tag should come back.
        SharedUserCtx.UserId = userBId;
        var resultB = (IOperationResult)await executor.ExecuteAsync(
            $"{{ storage {{ files(driveId: \"{driveId}\") {{ nodes {{ tags {{ nodes {{ key value }} }} }} }} }} }}");
        var tagsB = ExtractTags(resultB);
        Assert.Single(tagsB);
        Assert.Equal("owner", tagsB[0].Key);
        Assert.Equal("bob", tagsB[0].Value);
    }

    private static async Task<TestExecutor> BuildExecutorAsync(string dbName)
        => await GraphQlTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                services.AddSingleton<ITenantContext>(SharedTenantCtx);
                services.AddSingleton<ICurrentUser>(SharedUserCtx);
                services.AddDbContext<StrgDbContext>(o => o.UseInMemoryDatabase(dbName));
            },
            configureSchema: b =>
            {
                b.AddAuthorization()
                 .AddFiltering()
                 .AddType<RootQueryExtension>()
                 .AddType<StorageQueries>()
                 .AddType<FileQueries>()
                 .AddType<FileItemType>()
                 .AddType<FileVersionType>()
                 .AddType<TagType>()
                 .AddType<FileItemFilterInputType>()
                 .AddGlobalObjectIdentification()
                 // Nested-paging queries (files > nodes > tags > nodes) trip Hot Chocolate's
                 // default 1000-unit type-cost cap. Lift to 100k for the schema-shape coverage —
                 // production caps stay at the Program.cs defaults.
                 .ModifyCostOptions(o => { o.MaxTypeCost = 100_000; o.MaxFieldCost = 100_000; });
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            });

    private static Guid AddFile(StrgDbContext db, Guid tenantId, Guid driveId, string name)
    {
        var file = new FileItem
        {
            TenantId = tenantId,
            DriveId = driveId,
            Name = name,
            Path = name,
            CreatedBy = Guid.NewGuid(),
        };
        db.Files.Add(file);
        return file.Id;
    }

    private static int ExtractTotalCount(IOperationResult result)
    {
        using var doc = JsonDocument.Parse(result.ToJson());
        Assert.True(doc.RootElement.TryGetProperty("data", out var data),
            $"expected data, got: {result.ToJson()}");
        return data.GetProperty("storage").GetProperty("files").GetProperty("totalCount").GetInt32();
    }

    private static IReadOnlyList<(string Key, string Value)> ExtractTags(IOperationResult result)
    {
        using var doc = JsonDocument.Parse(result.ToJson());
        Assert.True(doc.RootElement.TryGetProperty("data", out var data),
            $"expected data, got: {result.ToJson()}");
        var pairs = new List<(string Key, string Value)>();
        foreach (var node in data.GetProperty("storage").GetProperty("files").GetProperty("nodes").EnumerateArray())
        {
            foreach (var tagNode in node.GetProperty("tags").GetProperty("nodes").EnumerateArray())
            {
                pairs.Add((tagNode.GetProperty("key").GetString()!, tagNode.GetProperty("value").GetString()!));
            }
        }
        return pairs;
    }
}
