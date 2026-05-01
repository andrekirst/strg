using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQl.DataLoaders;
using Strg.Infrastructure.Data;
using DomainTag = Strg.Core.Domain.Tag;

namespace Strg.GraphQl.Types;

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

        // STRG-340 — DataLoader-batched thumbnail field. Returns null when the consumer hasn't
        // yet produced a row for the (file, variant) pair; clients re-query when the
        // `thumbnailReady` subscription fires (or just retry after the REST 202's Retry-After).
        descriptor.Field("thumbnail")
            .Argument("variant", a => a.Type<NonNullType<EnumType<ThumbnailVariantGraphQl>>>())
            .Type<ThumbnailType>()
            .Resolve(async ctx =>
            {
                var file = ctx.Parent<FileItem>();
                var variant = ctx.ArgumentValue<ThumbnailVariantGraphQl>("variant");
                var loader = ctx.Service<ThumbnailDataLoader>();
                return await loader.LoadAsync(
                    new ThumbnailKey(file.Id, variant.ToVariantString()),
                    ctx.RequestAborted);
            });

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
