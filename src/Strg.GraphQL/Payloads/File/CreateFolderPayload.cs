using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.File;
public sealed record CreateFolderPayload(FileItem? File, IReadOnlyList<UserError>? Errors);
