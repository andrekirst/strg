namespace Strg.Infrastructure.Data.Configurations;

// public — cross-assembly access from Strg.WebDav.DbLockManager's constraint-name equality check.
// NotificationConstraintNames stays internal because its only consumer lives in the same assembly.
public static class FileLockConstraintNames
{
    // Name used by DbLockManager.LockAsync to discriminate the "another lock is already active"
    // unique-violation (SqlState 23505) from other constraint errors. A substring match on
    // "IX_FileLocks" would silently misclassify the token-lookup index if someone ever renamed
    // one of these — which is why both names are constants, pinned by the schema test, and
    // compared with equality in the consumer.
    public const string ActiveLockUniqueIndex = "IX_FileLocks_TenantId_ResourceUri_Active";

    public const string TokenIndex = "IX_FileLocks_Token";
}
