namespace Strg.Plugin.Abstractions.Search;

/// <summary>
/// Full-text search backend — Elasticsearch, Meilisearch, Lucene, or the in-tree EF default.
/// The host invokes <see cref="IndexAsync"/>/<see cref="DeleteAsync"/> from MassTransit consumers
/// listening on file-uploaded / file-deleted events; <see cref="SearchAsync"/> is invoked from
/// the REST and GraphQL search endpoints.
///
/// <para><b>Tenant scoping.</b> <see cref="SearchAsync"/> takes <c>tenantId</c> as the partition
/// key; provider implementations MUST scope every query to that tenant. Index writes
/// (<see cref="IndexAsync"/>/<see cref="DeleteAsync"/>) take only the file id because the host
/// has already verified tenant ownership before dispatching — but providers SHOULD persist the
/// tenant id alongside the document so a future cross-tenant query bug cannot leak content.</para>
///
/// <para><b>Idempotency.</b> <see cref="DeleteAsync"/> mirrors the
/// <see cref="Strg.Plugin.Abstractions.Storage.IStorageProvider.DeleteAsync"/> contract: no throw on missing. Re-indexing
/// the same file id MUST replace the existing document, not duplicate it.</para>
/// </summary>
public interface ISearchProvider : IStrgPlugin
{
    /// <summary>Stable identifier for the provider type; examples: <c>"default-ef"</c>, <c>"elasticsearch"</c>, <c>"meilisearch"</c>.</summary>
    string ProviderType { get; }

    /// <summary>
    /// Indexes (or replaces) the document for <paramref name="fileId"/>. The host pre-extracts
    /// plaintext from the underlying file and supplies it via <paramref name="textContent"/>;
    /// the provider owns language analysis and tokenisation. v0.2 will add a metadata
    /// dictionary parameter for non-textual facets.
    /// </summary>
    Task IndexAsync(Guid fileId, string textContent, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the document for <paramref name="fileId"/>. Idempotent — implementations MUST NOT
    /// throw if the document is absent.
    /// </summary>
    Task DeleteAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the search hits for <paramref name="query"/> within <paramref name="tenantId"/>'s
    /// partition. Pagination is via <see cref="SearchOptions.Limit"/> / <see cref="SearchOptions.Offset"/>;
    /// filters are opaque key/value pairs interpreted by the provider.
    /// </summary>
    Task<SearchResult> SearchAsync(
        Guid tenantId,
        string query,
        SearchOptions options,
        CancellationToken cancellationToken);
}
