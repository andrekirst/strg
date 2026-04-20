using Microsoft.EntityFrameworkCore;
using HotChocolate.Types;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Types;

public sealed class DriveType : ObjectType<Drive>
{
    protected override void Configure(IObjectTypeDescriptor<Drive> descriptor)
    {
        descriptor.ImplementsNode()
            .IdField(d => d.Id)
            .ResolveNode(async (ctx, id) =>
            {
                var db = ctx.Service<StrgDbContext>();
                return await db.Drives.FirstOrDefaultAsync(d => d.Id == id, ctx.RequestAborted);
            });

        descriptor.Field(d => d.ProviderConfig).Ignore();
        descriptor.Field(d => d.TenantId).Ignore();
    }
}
