namespace Strg.GraphQl.Payloads.File;

public sealed record DeleteFilePayload(Guid? FileId, IReadOnlyList<UserError>? Errors);
