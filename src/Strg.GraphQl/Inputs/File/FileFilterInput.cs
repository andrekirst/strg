namespace Strg.GraphQl.Inputs.File;

public sealed record FileFilterInput(
    string? NameContains,
    string? MimeType,
    bool? IsFolder,
    long? MinSize,
    long? MaxSize,
    DateTimeOffset? CreatedAfter,
    DateTimeOffset? CreatedBefore,
    string? TagKey,
    bool? IsInInbox
);
