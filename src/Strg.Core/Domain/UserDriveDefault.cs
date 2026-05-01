namespace Strg.Core.Domain;

/// <summary>
/// Per-user override of which drive a user considers "default" within a tenant. At most one row
/// per (TenantId, UserId). Falls back to the tenant-wide <see cref="Drive.IsDefault"/> drive when
/// no row exists. Read by GetDefaultDriveQuery; written by SetDefaultDriveCommand (upsert).
/// </summary>
public sealed class UserDriveDefault : TenantedEntity
{
    public Guid UserId { get; init; }
    public Guid DriveId { get; set; }
}
