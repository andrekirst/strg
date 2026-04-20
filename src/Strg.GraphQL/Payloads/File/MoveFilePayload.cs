using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.File;

public sealed record MoveFilePayload(FileItem? File, IReadOnlyList<UserError>? Errors);
