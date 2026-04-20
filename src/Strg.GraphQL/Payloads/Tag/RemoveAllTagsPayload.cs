namespace Strg.GraphQL.Payloads.Tag;

public sealed record RemoveAllTagsPayload(Guid? FileId, IReadOnlyList<UserError>? Errors);
