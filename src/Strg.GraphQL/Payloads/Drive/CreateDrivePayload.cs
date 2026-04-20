using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.Drive;

public sealed record CreateDrivePayload(Core.Domain.Drive? Drive, IReadOnlyList<UserError>? Errors);
