namespace Strg.GraphQL;

public static class Topics
{
    /// <summary>
    /// Per-(tenant, drive) fan-out topic for file domain events. Tenant-prefixed because driveIds,
    /// though UUIDs, routinely leak via shared URLs, audit logs, support-ticket screenshots, and
    /// ex-collaborator handoffs. A receiver-side-only filter (topic keyed on driveId alone)
    /// establishes the WebSocket channel for a cross-tenant subscriber holding a leaked driveId,
    /// and even though the per-event resolver guard blocks the payload, the channel itself is an
    /// <b>oracle</b>: the subscriber learns event cadence (idle vs busy drive), latency-correlated
    /// timing (every suppressed event = server work observable by the client), and the server
    /// log stream fills with per-event <see cref="UnauthorizedAccessException"/> entries —
    /// information a legitimate caller of a foreign-tenant drive would never have.
    /// </summary>
    /// <remarks>
    /// <para>Keying the topic on <c>(tenantId, driveId)</c> makes the cross-tenant subscribe path
    /// structurally empty: the subscriber connects to <c>file-events:{wrong-tenant}:{driveId}</c>
    /// and no events ever route there. The per-event resolver tenant guard
    /// (<see cref="Subscriptions.FileSubscriptions.FileEvents"/>) remains as defence-in-depth
    /// against a future bug that bypasses the topic key — e.g., a custom
    /// <c>SubscribeToFileEventsAsync</c> impl that does regex topic matching, or a breakage of
    /// <c>[GlobalState("tenantId")]</c> propagation on the resolver side.</para>
    ///
    /// <para><b>Drive transfer between tenants (future).</b> Drives don't cross tenants today, so
    /// <c>(tenantId, driveId)</c> is also safe-by-construction — two drives in different tenants
    /// cannot collide. If a future feature allows transferring a drive between tenants, the
    /// subscription topic key MUST move to the new tenant atomically with the transfer (or
    /// subscribers of the old tenant will keep receiving events post-transfer). That invariant
    /// lives on the drive-transfer feature, not here.</para>
    ///
    /// <para><b>Symmetry with <see cref="QuotaWarnings"/>.</b> Same rationale — both topics carry
    /// payloads that identify a principal (tenant-scoped), and both use a tenant prefix to turn
    /// receiver-side filtering from a deliver-time check (leaky) into a structural routing
    /// guarantee (empty channel on tenant mismatch).</para>
    /// </remarks>
    public static string FileEvents(Guid tenantId, Guid driveId) => $"file-events:{tenantId}:{driveId}";

    public static string InboxFileProcessed(Guid tenantId) => $"inbox-file-processed:{tenantId}";

    // Per-(tenant, user) topic: only the owner of the quota receives the warning. The tenant
    // prefix prevents a cross-tenant userId collision (theoretical under Guid.NewGuid but pinned
    // at the topic level for defence-in-depth) from leaking a warning to another tenant.
    public static string QuotaWarnings(Guid tenantId, Guid userId) => $"quota-warnings:{tenantId}:{userId}";
}
