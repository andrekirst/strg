namespace Strg.GraphQL.Payloads.Tag;

public sealed record UpdateTagPayload(Core.Domain.Tag? Tag, IReadOnlyList<UserError>? Errors);
