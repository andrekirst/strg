using Microsoft.EntityFrameworkCore;
using Strg.Infrastructure.Data;
using DomainTag = Strg.Core.Domain.Tag;

namespace Strg.GraphQL.Types;

public sealed class TagType : ObjectType<DomainTag>
{
    protected override void Configure(IObjectTypeDescriptor<DomainTag> descriptor)
    {
        descriptor.ImplementsNode()
            .IdField(t => t.Id)
            .ResolveNode(async (ctx, id) =>
            {
                var db = ctx.Service<StrgDbContext>();
                return await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ctx.RequestAborted);
            });

        descriptor.Field(t => t.TenantId).Ignore();
        descriptor.Field(t => t.UserId).Ignore();
        descriptor.Field(t => t.FileId).Ignore();
    }
}
