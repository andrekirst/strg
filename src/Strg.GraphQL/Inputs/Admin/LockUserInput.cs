namespace Strg.GraphQL.Inputs.Admin;
public sealed record LockUserInput(Guid UserId, string? Reason);
