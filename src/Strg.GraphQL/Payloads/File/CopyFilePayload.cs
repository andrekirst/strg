using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.File;
public sealed record CopyFilePayload(FileItem? File, IReadOnlyList<UserError>? Errors);
