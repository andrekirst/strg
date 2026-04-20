using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.File;
public sealed record RenameFilePayload(FileItem? File, IReadOnlyList<UserError>? Errors);
