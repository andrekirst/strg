namespace Strg.Core.Domain;

/// <summary>
/// Lifecycle state for a <see cref="ThumbnailEntry"/>. The four states are mutually exclusive
/// per (FileVersion, Variant, Format) tuple and govern how the REST/GraphQL surfaces respond:
/// <list type="bullet">
///   <item><c>Pending</c> — row inserted by the consumer; blob not yet written. REST returns 202.</item>
///   <item><c>Ready</c> — blob written and metadata populated. REST streams 200; ETag is strong.</item>
///   <item><c>Failed</c> — generator threw or timed out. REST returns 404 (no blob exists). Retryable via backfill.</item>
///   <item><c>Unsupported</c> — the source MIME / safeguard / encrypted-drive carve-out forbids generation. REST returns 404.</item>
/// </list>
/// </summary>
public enum ThumbnailStatus
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
    Unsupported = 3,
}
