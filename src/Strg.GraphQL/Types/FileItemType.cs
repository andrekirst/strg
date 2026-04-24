using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using DomainTag = Strg.Core.Domain.Tag;

namespace Strg.GraphQL.Types;

public sealed class FileItemType : ObjectType<FileItem>
{
    protected override void Configure(IObjectTypeDescriptor<FileItem> descriptor)
    {
        descriptor.ImplementsNode()
            .IdField(f => f.Id)
            .ResolveNode(async (ctx, id) =>
            {
                var db = ctx.Service<StrgDbContext>();
                return await db.Files.FirstOrDefaultAsync(f => f.Id == id, ctx.RequestAborted);
            });

        descriptor.Field(f => f.TenantId).Ignore();
        descriptor.Field(f => f.StorageKey).Ignore();
        descriptor.Field(f => f.IsDirectory).Ignore();

        descriptor.Field("children")
            .UsePaging<ObjectType<FileItem>>(options: new() { DefaultPageSize = 50, MaxPageSize = 200 })
            .Resolve(ctx =>
            {
                var file = ctx.Parent<FileItem>();
                var db = ctx.Service<StrgDbContext>();
                return db.Files.Where(f => f.ParentId == file.Id);
            });

        descriptor.Field("tags")
            .UsePaging<ObjectType<DomainTag>>(options: new() { DefaultPageSize = 100, MaxPageSize = 500 })
            .Resolve(ctx =>
            {
                var file = ctx.Parent<FileItem>();
                var db = ctx.Service<StrgDbContext>();
                return db.Tags.Where(t => t.FileId == file.Id);
            });

        descriptor.Field("versions")
            .UsePaging<ObjectType<FileVersion>>(options: new() { DefaultPageSize = 20, MaxPageSize = 100 })
            .Resolve(ctx =>
            {
                var file = ctx.Parent<FileItem>();
                var db = ctx.Service<StrgDbContext>();
                return db.FileVersions.Where(v => v.FileId == file.Id).OrderByDescending(v => v.VersionNumber);
            });
    }
}
