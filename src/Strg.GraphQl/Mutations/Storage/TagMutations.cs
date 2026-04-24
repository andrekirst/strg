using HotChocolate.Authorization;
using Mediator;
using Strg.Application.Features.Tags.AddTag;
using Strg.Application.Features.Tags.RemoveAllTags;
using Strg.Application.Features.Tags.RemoveTag;
using Strg.Application.Features.Tags.UpdateTag;
using Strg.GraphQl.Inputs.Tag;
using Strg.GraphQl.Payloads;
using Strg.GraphQl.Payloads.Tag;

namespace Strg.GraphQl.Mutations.Storage;

[ExtendObjectType<StorageMutations>]
public sealed class TagMutations
{
    [Authorize(Policy = "TagsWrite")]
    public async Task<AddTagPayload> AddTagAsync(
        AddTagInput input,
        [Service] IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new AddTagCommand(input.FileId, input.Key, input.Value, input.ValueType),
            cancellationToken);

        return result.IsSuccess
            ? new AddTagPayload(result.Value, null)
            : new AddTagPayload(null, [new UserError(MapCode(result.ErrorCode!), result.ErrorMessage!, FieldFor(result))]);
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<UpdateTagPayload> UpdateTagAsync(
        UpdateTagInput input,
        [Service] IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new UpdateTagCommand(input.Id, input.Value, input.ValueType),
            cancellationToken);

        return result.IsSuccess
            ? new UpdateTagPayload(result.Value, null)
            : new UpdateTagPayload(null, [new UserError(MapCode(result.ErrorCode!), result.ErrorMessage!, FieldFor(result))]);
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<RemoveTagPayload> RemoveTagAsync(
        RemoveTagInput input,
        [Service] IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RemoveTagCommand(input.Id), cancellationToken);

        return result.IsSuccess
            ? new RemoveTagPayload(result.Value, null)
            : new RemoveTagPayload(input.Id, [new UserError(MapCode(result.ErrorCode!), result.ErrorMessage!, null)]);
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<RemoveAllTagsPayload> RemoveAllTagsAsync(
        RemoveAllTagsInput input,
        [Service] IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RemoveAllTagsCommand(input.FileId), cancellationToken);

        return result.IsSuccess
            ? new RemoveAllTagsPayload(input.FileId, null)
            : new RemoveAllTagsPayload(input.FileId, [new UserError(MapCode(result.ErrorCode!), result.ErrorMessage!, null)]);
    }

    // Translates handler-side Result error codes (PascalCase) into the legacy uppercase-snake
    // wire codes the existing GraphQL consumers expect. Keeps the Phase 2 migration
    // wire-compatible even though Strg.Application has its own codes internally.
    private static string MapCode(string code) => code switch
    {
        "ValidationError" => "VALIDATION_ERROR",
        "NotFound" => "NOT_FOUND",
        _ => code,
    };

    // Validation failures carry the offending property in the error message (formatted as
    // "PropertyName: message; ..." by ValidationBehavior). Probe for Key / Value tokens and
    // report the camelCased GraphQL input field name so clients can highlight the right control.
    private static string? FieldFor<T>(Strg.Core.Result<T> result)
    {
        if (result.ErrorCode != "ValidationError" || result.ErrorMessage is null)
        {
            return null;
        }
        if (result.ErrorMessage.Contains("Key", StringComparison.Ordinal))
        {
            return "key";
        }
        if (result.ErrorMessage.Contains("Value", StringComparison.Ordinal))
        {
            return "value";
        }
        return null;
    }
}
