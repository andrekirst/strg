namespace Strg.GraphQl.Payloads.Drive;

public sealed record SetDefaultDrivePayload(Core.Domain.Drive? Drive, IReadOnlyList<UserError>? Errors);
