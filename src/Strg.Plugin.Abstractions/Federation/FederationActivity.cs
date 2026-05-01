namespace Strg.Plugin.Abstractions.Federation;

/// <summary>
/// Minimal protocol-neutral envelope for an outbound federation activity. The shape mirrors
/// ActivityPub semantics — <see cref="Type"/> (e.g. <c>"Create"</c>, <c>"Follow"</c>),
/// <see cref="Actor"/> (the originating actor URI), <see cref="ObjectId"/> (the activity's
/// object URI) — but is generic enough to carry analogous fields for other protocols. Provider-
/// specific extension data goes in <see cref="Payload"/>.
/// </summary>
public sealed record FederationActivity(
    string Type,
    string Actor,
    string ObjectId,
    IReadOnlyDictionary<string, object?>? Payload);
