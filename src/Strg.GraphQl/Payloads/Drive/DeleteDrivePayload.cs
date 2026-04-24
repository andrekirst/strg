namespace Strg.GraphQl.Payloads.Drive;

public sealed record DeleteDrivePayload(Guid? DriveId, IReadOnlyList<UserError>? Errors);
