namespace Strg.GraphQL.Inputs.Admin;
public sealed record AuditFilterInput(Guid? UserId, string? Action, string? ResourceType, DateTimeOffset? From, DateTimeOffset? To);
