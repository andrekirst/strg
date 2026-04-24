namespace Strg.GraphQl.Payloads.Tag;

public sealed record RemoveAllTagsPayload(Guid? FileId, IReadOnlyList<UserError>? Errors);
