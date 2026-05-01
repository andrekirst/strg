namespace Strg.Core.Services;

/// <summary>
/// Orchestrator surface the consumer talks to. Fans out to N <see cref="IThumbnailGenerator"/>
/// invocations (one per requested variant) and writes the resulting <see cref="Domain.ThumbnailEntry"/>
/// rows + blobs.
///
/// <para><b>Why an indirection?</b> <c>ThumbnailGenerationConsumer</c> handles two event types
/// (<c>FileUploadedEvent</c> + <c>ThumbnailGenerationRequestedEvent</c>) that share identical
/// orchestration but differ in payload shape. The service hides the per-variant fan-out and
/// idempotency logic from the consumer's two <c>Consume</c> entrypoints.</para>
/// </summary>
public interface IThumbnailService
{
    /// <summary>
    /// Generate every variant in <paramref name="variants"/> for the file at
    /// <paramref name="fileVersionId"/>. Idempotent: a re-delivery finds the rows already at
    /// <see cref="Domain.ThumbnailStatus.Ready"/> / <see cref="Domain.ThumbnailStatus.Unsupported"/>
    /// and returns without re-running the generator.
    /// </summary>
    Task GenerateAllAsync(
        Guid tenantId,
        Guid fileId,
        Guid fileVersionId,
        Guid driveId,
        IReadOnlyList<string> variants,
        CancellationToken cancellationToken);
}
