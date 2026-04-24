using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Types;

public sealed class FileVersionType : ObjectType<FileVersion>
{
    protected override void Configure(IObjectTypeDescriptor<FileVersion> descriptor)
    {
        descriptor.ImplementsNode()
            .IdField(v => v.Id)
            .ResolveNode(async (ctx, id) =>
            {
                var db = ctx.Service<StrgDbContext>();
                return await db.FileVersions.FirstOrDefaultAsync(v => v.Id == id, ctx.RequestAborted);
            });

        descriptor.Field(v => v.StorageKey).Ignore();
        descriptor.Field(v => v.FileId).Ignore();
    }
}
