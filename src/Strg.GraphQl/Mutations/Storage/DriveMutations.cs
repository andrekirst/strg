using HotChocolate.Authorization;
using Mediator;
using Strg.Application.Features.Drives.Create;
using Strg.Application.Features.Drives.Delete;
using Strg.Application.Features.Drives.Update;
using Strg.GraphQl.Inputs.Drive;
using Strg.GraphQl.Payloads;
using Strg.GraphQl.Payloads.Drive;

namespace Strg.GraphQl.Mutations.Storage;

[ExtendObjectType<StorageMutations>]
public sealed class DriveMutations
{
    [Authorize(Policy = "Admin")]
    public async Task<CreateDrivePayload> CreateDriveAsync(
        CreateDriveInput input,
        [Service] IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new CreateDriveCommand(
                input.Name,
                input.ProviderType,
                input.ProviderConfig,
                input.IsEncrypted ?? false,
                input.IsDefault),
            cancellationToken);

        return result.IsSuccess
            ? new CreateDrivePayload(result.Value, null)
            : new CreateDrivePayload(null, [new UserError(MapCode(result.ErrorCode!), result.ErrorMessage!, FieldFor(result))]);
    }

    [Authorize(Policy = "Admin")]
    public async Task<UpdateDrivePayload> UpdateDriveAsync(
        UpdateDriveInput input,
        [Service] IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new UpdateDriveCommand(input.Id, input.Name, input.IsDefault),
            cancellationToken);

        return result.IsSuccess
            ? new UpdateDrivePayload(result.Value, null)
            : new UpdateDrivePayload(null, [new UserError(MapCode(result.ErrorCode!), result.ErrorMessage!, FieldFor(result))]);
    }

    [Authorize(Policy = "Admin")]
    public async Task<DeleteDrivePayload> DeleteDriveAsync(
        DeleteDriveInput input,
        [Service] IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteDriveCommand(input.Id), cancellationToken);

        return result.IsSuccess
            ? new DeleteDrivePayload(result.Value, null)
            : new DeleteDrivePayload(null, [new UserError(MapCode(result.ErrorCode!), result.ErrorMessage!, null)]);
    }

    // Translates handler-side Result error codes (PascalCase) into the legacy uppercase-snake
    // wire codes the existing GraphQL consumers expect. DuplicateName maps to the pre-migration
    // DUPLICATE_DRIVE_NAME identifier so existing clients pattern-matching on it keep working.
    private static string MapCode(string code) => code switch
    {
        "ValidationError" => "VALIDATION_ERROR",
        "NotFound" => "NOT_FOUND",
        "DuplicateName" => "DUPLICATE_DRIVE_NAME",
        "InvalidProviderType" => "INVALID_PROVIDER_TYPE",
        _ => code,
    };

    // Validation failures carry the offending property in the message. Match substrings rather
    // than the exact command field name because FluentValidation emits properties in PascalCase
    // while the GraphQL wire uses camelCase — and the input-vs-command fields don't always match
    // (e.g. command.ProviderConfigJson vs input.providerConfig).
    private static string? FieldFor<T>(Strg.Core.Result<T> result)
    {
        if (result.ErrorCode == "DuplicateName" || result.ErrorCode == "InvalidProviderType")
        {
            return "name";
        }
        if (result.ErrorCode != "ValidationError" || result.ErrorMessage is null)
        {
            return null;
        }
        if (result.ErrorMessage.Contains("ProviderConfigJson", StringComparison.Ordinal))
        {
            return "providerConfig";
        }
        if (result.ErrorMessage.Contains("ProviderType", StringComparison.Ordinal))
        {
            return "providerType";
        }
        if (result.ErrorMessage.Contains("Name", StringComparison.Ordinal))
        {
            return "name";
        }
        return null;
    }
}
