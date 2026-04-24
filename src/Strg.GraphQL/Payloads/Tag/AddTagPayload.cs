namespace Strg.GraphQL.Payloads.Tag;

public sealed record AddTagPayload(Core.Domain.Tag? Tag, IReadOnlyList<UserError>? Errors);
