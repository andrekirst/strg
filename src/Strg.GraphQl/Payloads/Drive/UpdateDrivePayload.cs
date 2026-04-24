namespace Strg.GraphQl.Payloads.Drive;

public sealed record UpdateDrivePayload(Core.Domain.Drive? Drive, IReadOnlyList<UserError>? Errors);
