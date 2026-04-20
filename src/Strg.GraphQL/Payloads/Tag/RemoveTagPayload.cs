namespace Strg.GraphQL.Payloads.Tag;
public sealed record RemoveTagPayload(Guid? TagId, IReadOnlyList<UserError>? Errors);
