namespace Strg.GraphQl.Payloads.User;

public sealed record ChangePasswordPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
