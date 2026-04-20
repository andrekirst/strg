using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.User;

public sealed record UnlockUserPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
