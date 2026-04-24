namespace Strg.GraphQl.Inputs.Admin;

public sealed record LockUserInput(Guid UserId, string? Reason);
