namespace Strg.GraphQl.Payloads.User;

public sealed record UpdateUserQuotaPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
