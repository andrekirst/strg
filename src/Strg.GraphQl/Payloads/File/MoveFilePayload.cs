using Strg.Core.Domain;
namespace Strg.GraphQl.Payloads.File;

public sealed record MoveFilePayload(FileItem? File, IReadOnlyList<UserError>? Errors);
