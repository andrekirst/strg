namespace Strg.GraphQL.Payloads.File;
public sealed record DeleteFilePayload(Guid? FileId, IReadOnlyList<UserError>? Errors);
