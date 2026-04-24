using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Types;

public sealed class UserType : ObjectType<User>
{
    protected override void Configure(IObjectTypeDescriptor<User> descriptor)
    {
        descriptor.ImplementsNode()
            .IdField(u => u.Id)
            .ResolveNode(async (ctx, id) =>
            {
                var db = ctx.Service<StrgDbContext>();
                return await db.Users.FirstOrDefaultAsync(u => u.Id == id, ctx.RequestAborted);
            });

        descriptor.Field(u => u.TenantId).Ignore();
        descriptor.Field(u => u.PasswordHash).Ignore();
        descriptor.Field(u => u.LockedUntil).Ignore();
    }
}
