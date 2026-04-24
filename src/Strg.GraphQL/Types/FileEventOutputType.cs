using Strg.Core.Domain;
using Strg.GraphQL.DataLoaders;
using Strg.GraphQL.Subscriptions.Payloads;

namespace Strg.GraphQL.Types;

public sealed class FileEventOutputType : ObjectType<FileEventPayload>
{
    protected override void Configure(IObjectTypeDescriptor<FileEventPayload> descriptor)
    {
        descriptor.Field(e => e.EventType);
        descriptor.Field("file")
            .Type<ObjectType<FileItem>>()
            .Resolve(async ctx =>
            {
                var payload = ctx.Parent<FileEventPayload>();
                var loader = ctx.Service<FileItemByIdDataLoader>();
                return await loader.LoadAsync(payload.FileId, ctx.RequestAborted);
            });
        descriptor.Field(e => e.DriveId);
        descriptor.Field(e => e.OccurredAt);
    }
}
