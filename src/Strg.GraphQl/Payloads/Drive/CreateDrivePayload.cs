namespace Strg.GraphQl.Payloads.Drive;

public sealed record CreateDrivePayload(Core.Domain.Drive? Drive, IReadOnlyList<UserError>? Errors);
