namespace Strg.Plugin.Abstractions.Tagging;

/// <summary>
/// AI-driven auto-tag suggester. Invoked by the host after upload (or on-demand from the admin
/// UI); returned suggestions are filtered by a host-side confidence threshold before persisting.
///
/// <para><b>Content access is opt-in.</b> The host passes <paramref name="content"/> as
/// <c>null</c> when the plugin's manifest does not declare the read-content permission, when the
/// file is encrypted at rest and the plugin is not authorised to decrypt, or when the host
/// chooses metadata-only tagging for performance reasons. Plugins MUST handle the null case
/// gracefully — typically by returning suggestions derived solely from <paramref name="mimeType"/>
/// and the file id (e.g. via an external metadata lookup).</para>
///
/// <para><b>MIME-type short-circuit.</b> Plugins MUST return an empty list immediately for
/// unsupported MIME types rather than scanning the stream and failing late. The host classifies
/// the MIME type once at upload; downstream consumers depend on it being authoritative.</para>
/// </summary>
public interface IAITagger : IStrgPlugin
{
    /// <summary>
    /// Suggests tags for the file identified by <paramref name="fileId"/>. <paramref name="content"/>
    /// is the plaintext stream when the plugin is permitted to read it, otherwise <c>null</c>.
    /// </summary>
    Task<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        Guid fileId,
        Stream? content,
        string mimeType,
        CancellationToken cancellationToken);
}
