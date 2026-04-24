namespace Strg.GraphQl.Payloads.User;

public sealed record UpdateProfilePayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
