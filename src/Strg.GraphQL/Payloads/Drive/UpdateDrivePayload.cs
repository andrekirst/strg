namespace Strg.GraphQL.Payloads.Drive;

public sealed record UpdateDrivePayload(Core.Domain.Drive? Drive, IReadOnlyList<UserError>? Errors);
