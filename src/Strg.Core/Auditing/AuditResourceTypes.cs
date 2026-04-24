namespace Strg.Core.Auditing;

/// <summary>
/// Canonical <see cref="Domain.AuditEntry.ResourceType"/> strings. Sibling of
/// <see cref="AuditActions"/>; callers choose a value from here rather than hand-writing the
/// resource-type literal at each call site.
/// </summary>
public static class AuditResourceTypes
{
    public const string Drive = "Drive";
    public const string FileItem = "FileItem";
    public const string User = "User";
}
