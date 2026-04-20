namespace Strg.GraphQL;

public static class Topics
{
    public static string FileEvents(Guid driveId) => $"file-events:{driveId}";
    public static string InboxFileProcessed(Guid tenantId) => $"inbox-file-processed:{tenantId}";
}
