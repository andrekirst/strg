using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.User;
public sealed record LockUserPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
