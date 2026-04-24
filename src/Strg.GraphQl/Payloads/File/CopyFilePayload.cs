using Strg.Core.Domain;
namespace Strg.GraphQl.Payloads.File;

public sealed record CopyFilePayload(FileItem? File, IReadOnlyList<UserError>? Errors);
