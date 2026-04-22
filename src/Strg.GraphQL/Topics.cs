namespace Strg.GraphQL;

public static class Topics
{
    public static string FileEvents(Guid driveId) => $"file-events:{driveId}";
    public static string InboxFileProcessed(Guid tenantId) => $"inbox-file-processed:{tenantId}";

    // Per-(tenant, user) topic: only the owner of the quota receives the warning. The tenant
    // prefix prevents a cross-tenant userId collision (theoretical under Guid.NewGuid but pinned
    // at the topic level for defence-in-depth) from leaking a warning to another tenant.
    public static string QuotaWarnings(Guid tenantId, Guid userId) => $"quota-warnings:{tenantId}:{userId}";
}
