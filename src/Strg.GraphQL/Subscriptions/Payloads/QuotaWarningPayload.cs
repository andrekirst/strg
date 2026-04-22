namespace Strg.GraphQL.Subscriptions.Payloads;

/// <summary>
/// Subscription payload for <c>onQuotaWarning</c>. Carries the level discriminator + the raw
/// usage bytes so the client can render either a "plan cleanup" (warning) or "next upload will
/// likely fail" (critical) banner without round-tripping back for detail.
///
/// <para>No <c>TenantId</c> or <c>UserId</c>: the topic is already per-(tenant, user), so both
/// are implicit in the subscription binding. Including them in the payload would invite a
/// client to display someone else's warning if a topic-routing bug ever leaked cross-user.</para>
/// </summary>
public sealed record QuotaWarningPayload(
    string Level,
    long UsedBytes,
    long QuotaBytes,
    DateTimeOffset OccurredAt
);
