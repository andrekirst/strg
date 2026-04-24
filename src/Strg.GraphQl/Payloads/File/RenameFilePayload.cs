using Strg.Core.Domain;
namespace Strg.GraphQl.Payloads.File;

public sealed record RenameFilePayload(FileItem? File, IReadOnlyList<UserError>? Errors);
