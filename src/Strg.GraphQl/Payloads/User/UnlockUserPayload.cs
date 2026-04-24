namespace Strg.GraphQl.Payloads.User;

public sealed record UnlockUserPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
