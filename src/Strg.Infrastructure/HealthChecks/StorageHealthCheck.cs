using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Strg.Core.Domain;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.HealthChecks;

/// <summary>
/// Readiness probe for the storage subsystem (STRG-008). Verifies that the default local-FS
/// drive is provisioned and that its provider can complete a write+delete round-trip on a
/// per-process sentinel key.
///
/// <para><b>Why this isn't tagged with the Drive's per-tenant identity:</b> Kubernetes probes
/// run unauthenticated, so <c>ITenantContext.TenantId</c> is <see cref="Guid.Empty"/> here.
/// The query uses <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters"/>
/// (carve-out per CLAUDE.md §Security #1, same shape as
/// <c>UserRepository.GetByEmailAsync</c>) and re-applies <c>DeletedAt == null</c> inline so soft-deleted
/// drives are still excluded.</para>
/// </summary>
public sealed class StorageHealthCheck : IHealthCheck
{
    private const string LocalProviderType = "local";

    private readonly StrgDbContext _db;
    private readonly IStorageProviderRegistry _providerRegistry;
    private readonly ILogger<StorageHealthCheck> _logger;

    public StorageHealthCheck(
        StrgDbContext db,
        IStorageProviderRegistry providerRegistry,
        ILogger<StorageHealthCheck> logger)
    {
        _db = db;
        _providerRegistry = providerRegistry;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        Drive? defaultLocalDrive;
        try
        {
            // Health probe runs unauthenticated — ITenantContext.TenantId is Guid.Empty, so the
            // EF Core tenant filter would hide every Drive. Carve-out per CLAUDE.md §Security #1
            // (same pattern as UserRepository.GetByEmailAsync). IsDeleted is computed (DeletedAt
            // backed) so the soft-delete filter is re-applied inline via DeletedAt == null.
            defaultLocalDrive = await _db.Drives
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    d => d.IsDefault && d.ProviderType == LocalProviderType && d.DeletedAt == null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The DB-check has its own entry in /health/ready and will surface the outage as
            // Unhealthy → 503. Returning Healthy here keeps the same incident from showing up
            // twice in the JSON response.
            if (ex is OperationCanceledException)
            {
                throw;
            }
            _logger.LogDebug(ex, "Storage health check skipped: database unreachable");
            return HealthCheckResult.Healthy("database unreachable; storage check skipped");
        }

        if (defaultLocalDrive is null)
        {
            return HealthCheckResult.Degraded(
                "no default local drive provisioned (operator must create one)");
        }

        // Defence-in-depth: data is not currently serialized by SafeHealthCheckResponseWriter
        // (only `status`/`description` reach the wire), but if a future maintainer swaps the
        // writer back to UIResponseWriter or a debugging variant, anything in `data` ships to
        // unauthenticated K8s probes. Drive Id is a tenant-scoped GUID — keep it OUT of `data`
        // and let operators read the same detail from the structured warning log below.
        var data = new Dictionary<string, object>
        {
            ["provider_type"] = defaultLocalDrive.ProviderType,
        };

        try
        {
            var providerConfig = ParseProviderConfig(defaultLocalDrive.ProviderConfig);
            var provider = _providerRegistry.Resolve(defaultLocalDrive.ProviderType, providerConfig);

            // Per-process sentinel name avoids collisions when multiple replicas share a mount.
            var sentinel = $".strg-healthcheck-{Environment.ProcessId}";
            using (var emptyStream = new MemoryStream(Array.Empty<byte>()))
            {
                await provider.WriteAsync(sentinel, emptyStream, cancellationToken).ConfigureAwait(false);
            }
            await provider.DeleteAsync(sentinel, cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy("storage probe succeeded", data);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            // SECURITY (STRG-008 Security Checklist): do NOT pass `exception: ex` to
            // HealthCheckResult — UIResponseWriter serializes Exception.Message into the JSON
            // body, and IOException.Message from LocalFileSystemProvider.WriteAsync typically
            // contains the full filesystem path (e.g. "Access to the path
            // '/var/lib/strg/drives/.../.strg-healthcheck-12345' is denied"). Logging carries
            // the diagnostic detail to operators; the wire response stays opaque.
            _logger.LogWarning(
                ex,
                "Storage health probe failed for drive {DriveId} (provider {ProviderType})",
                defaultLocalDrive.Id,
                defaultLocalDrive.ProviderType);

            return HealthCheckResult.Unhealthy("storage write probe failed", data: data);
        }
    }

    /// <summary>
    /// Parses the drive's <see cref="Drive.ProviderConfig"/> JSON (flat string→string map in
    /// v0.1) into a <see cref="DictionaryStorageProviderConfig"/>. Mirrors the JsonDocument-based
    /// approach from <c>FileVersionStore.ResolveProvider</c> (which tolerates non-string values)
    /// rather than the stricter
    /// <c>JsonSerializer.Deserialize&lt;Dictionary&lt;string,string?&gt;&gt;</c> used in
    /// <c>StrgWebDavStore.ParseProviderConfig</c> — kept inline because the JSON shape is an
    /// internal contract that doesn't yet warrant a shared abstraction.
    /// </summary>
    private static IStorageProviderConfig ParseProviderConfig(string providerConfigJson)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(providerConfigJson))
        {
            return new DictionaryStorageProviderConfig(values);
        }

        using var json = JsonDocument.Parse(providerConfigJson);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new DictionaryStorageProviderConfig(values);
        }

        foreach (var property in json.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText(),
            };
        }

        return new DictionaryStorageProviderConfig(values);
    }
}
