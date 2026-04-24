namespace Strg.GraphQl.Payloads.User;

public sealed record LockUserPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
