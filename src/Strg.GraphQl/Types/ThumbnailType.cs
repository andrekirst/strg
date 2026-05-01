using Strg.Core.Domain;

namespace Strg.GraphQl.Types;

/// <summary>
/// GraphQL projection of <see cref="Strg.Core.Domain.ThumbnailEntry"/>. <see cref="Url"/> always
/// points to the REST endpoint (single source of truth for byte streaming) — GraphQL is pure
/// metadata. <see cref="Status"/> mirrors the domain enum 1:1.
/// </summary>
public sealed record Thumbnail(
    string Url,
    int? Width,
    int? Height,
    long? SizeBytes,
    ThumbnailStatusGraphQl Status,
    string? Format,
    string? ErrorReason);

/// <summary>
/// Wire-level status enum for the GraphQL surface. Numerically aligned with
/// <see cref="ThumbnailStatus"/> so the cast in <see cref="ThumbnailStatusMap.FromDomain"/> is a
/// no-op; the explicit conversion centralises a future drift point.
/// </summary>
public enum ThumbnailStatusGraphQl
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
    Unsupported = 3,
}

public static class ThumbnailStatusMap
{
    public static ThumbnailStatusGraphQl FromDomain(ThumbnailStatus status) =>
        (ThumbnailStatusGraphQl)status;
}

/// <summary>
/// Wire-level variant enum. Hot Chocolate emits this as a GraphQL enum
/// <c>ThumbnailVariantGraphQl { THUMB, SMALL, MEDIUM }</c>. The string mapping happens at the
/// resolver edge via <see cref="ToVariantString"/>.
/// </summary>
public enum ThumbnailVariantGraphQl
{
    Thumb,
    Small,
    Medium,
}

public static class ThumbnailVariantGraphQlExtensions
{
    public static string ToVariantString(this ThumbnailVariantGraphQl variant) => variant switch
    {
        ThumbnailVariantGraphQl.Thumb => "thumb",
        ThumbnailVariantGraphQl.Small => "small",
        ThumbnailVariantGraphQl.Medium => "medium",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown variant"),
    };
}

public sealed class ThumbnailType : ObjectType<Thumbnail>
{
    protected override void Configure(IObjectTypeDescriptor<Thumbnail> descriptor)
    {
        descriptor.Field(t => t.Url).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.Status).Type<NonNullType<EnumType<ThumbnailStatusGraphQl>>>();
        descriptor.Field(t => t.ErrorReason)
            .Description("Human-readable reason when status is FAILED or UNSUPPORTED. Null otherwise.");
    }
}
