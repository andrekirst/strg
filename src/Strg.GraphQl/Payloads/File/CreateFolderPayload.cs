using Strg.Core.Domain;
namespace Strg.GraphQl.Payloads.File;

public sealed record CreateFolderPayload(FileItem? File, IReadOnlyList<UserError>? Errors);
