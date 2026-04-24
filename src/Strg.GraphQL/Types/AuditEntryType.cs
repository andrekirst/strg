using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQL.DataLoaders;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Types;

public sealed class AuditEntryType : ObjectType<AuditEntry>
{
    protected override void Configure(IObjectTypeDescriptor<AuditEntry> descriptor)
    {
        descriptor.ImplementsNode()
            .IdField(e => e.Id)
            .ResolveNode(async (ctx, id) =>
            {
                var db = ctx.Service<StrgDbContext>();
                return await db.AuditEntries.FirstOrDefaultAsync(e => e.Id == id, ctx.RequestAborted);
            });

        descriptor.Field(e => e.TenantId).Ignore();
        descriptor.Field(e => e.UserId).Ignore();

        descriptor.Field("performedBy")
            .Type<ObjectType<User>>()
            .Resolve(async ctx =>
            {
                var entry = ctx.Parent<AuditEntry>();
                var loader = ctx.Service<UserByIdDataLoader>();
                return await loader.LoadAsync(entry.UserId, ctx.RequestAborted);
            });
    }
}
