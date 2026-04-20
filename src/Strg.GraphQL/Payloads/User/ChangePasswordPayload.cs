using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.User;
public sealed record ChangePasswordPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
