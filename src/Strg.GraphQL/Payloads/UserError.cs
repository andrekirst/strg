namespace Strg.GraphQL.Payloads;

public sealed record UserError(string Code, string Message, string? Field);
