using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Migrations;

internal sealed class FixedTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

/// <summary>
/// Pins the STRG-005 acceptance criteria: the InitialCreate migration applies cleanly to a fresh
/// PostgreSQL, is idempotent on a second run, produces the expected schema shape, and the
/// <c>CK_Tags_ValueType</c> check constraint actually fires at the DB level. Uses a real
/// Testcontainers Postgres because the assertions observe relational-layer state
/// (<c>information_schema</c>, check-constraint enforcement) that an EF in-memory provider would
/// silently no-op.
///
/// <para>Test-cases map to STRG-005 spec: TC-001 (fresh apply), TC-002 (idempotency), TC-003
/// (User round-trip), TC-004 (FileKeys table + column shape), TC-005 (OpenIddict tables — spec
/// originally listed MassTransit outbox here, but MassTransit is deferred to Tranche 5; the v0.1
/// wire-level auth tables that this migration MUST carry are OpenIddict's), TC-006 (CK_Tags_ValueType).</para>
/// </summary>
public sealed class MigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task TC001_MigrateAsync_on_fresh_postgres_creates_all_expected_tables()
    {
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        var tables = await QueryTableNamesAsync(ctx);

        // Domain tables actually defined at HEAD. Deferred entities (file_locks,
        // file_versioning_overrides, notifications, acl_entries, shares) are NOT in the v0.1
        // migration because their entities do not exist yet — see Tranche 5/6/7 trackers.
        tables.Should().Contain(new[]
        {
            "Tenants", "Users", "Drives", "Files", "FileVersions", "FileKeys",
            "Tags", "AuditEntries", "InboxRules",
        });

        // OpenIddict tables — v0.1 token endpoint depends on these.
        tables.Should().Contain(new[]
        {
            "OpenIddictApplications", "OpenIddictScopes",
            "OpenIddictAuthorizations", "OpenIddictTokens",
        });

        // EF Core's own migration-history table.
        tables.Should().Contain("__EFMigrationsHistory");
    }

    [Fact]
    public async Task TC002_MigrateAsync_twice_is_idempotent()
    {
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        // Second call must be a no-op: EF reads __EFMigrationsHistory, finds InitialCreate already
        // applied, and returns without issuing DDL. If a future change makes the migration
        // non-idempotent (e.g. by hand-editing with a raw CREATE TABLE without IF NOT EXISTS),
        // this call throws.
        var secondRun = async () => await ctx.Database.MigrateAsync();
        await secondRun.Should().NotThrowAsync("MigrateAsync must be a no-op on an already-migrated database");
    }

    [Fact]
    public async Task TC003_User_round_trip_preserves_all_columns()
    {
        // Align the ambient tenant with the tenant we insert so the global tenant filter on the
        // User query below doesn't filter the row out — without the explicit Id assignment
        // EF's default-Guid for Tenant would diverge from ITenantContext.TenantId.
        var tenantId = Guid.NewGuid();
        await using var ctx = await BuildContextForFreshDatabaseAsync(tenantId);
        await ctx.Database.MigrateAsync();

        var tenant = new Tenant { Id = tenantId, Name = "acme" };
        ctx.Tenants.Add(tenant);
        await ctx.SaveChangesAsync();

        var user = new User
        {
            TenantId = tenantId,
            Email = "alice@acme.test",
            PasswordHash = "pbkdf2$sha256$310000$AAECAwQFBgc$deadbeef",
            DisplayName = "Alice",
            QuotaBytes = 10_000_000,
            UsedBytes = 42,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        // Detach so the read below is an actual DB roundtrip, not an identity-map hit.
        ctx.ChangeTracker.Clear();

        var loaded = await ctx.Users.SingleAsync(u => u.Id == user.Id);
        loaded.TenantId.Should().Be(tenant.Id);
        loaded.Email.Should().Be("alice@acme.test");
        loaded.PasswordHash.Should().Be(user.PasswordHash);
        loaded.DisplayName.Should().Be("Alice");
        loaded.QuotaBytes.Should().Be(10_000_000);
        loaded.UsedBytes.Should().Be(42);
        loaded.CreatedAt.Should().BeCloseTo(user.CreatedAt, TimeSpan.FromSeconds(1));
        loaded.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task TC004_FileKeys_table_has_expected_column_shape_and_unique_FileVersionId()
    {
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        var columns = await QueryColumnsAsync(ctx, "FileKeys");
        columns.Should().Contain("Id");
        columns.Should().Contain("FileVersionId");
        columns.Should().Contain("EncryptedDek");
        columns.Should().Contain("Algorithm");
        columns.Should().Contain("CreatedAt");

        // One FileKey per FileVersion (one DEK per blob envelope) — enforced by a unique index
        // on FileVersionId. If that index is dropped, a future refactor could silently insert
        // duplicate keys and the read-side algorithm dispatch would pick an arbitrary one.
        var uniqueIndexes = await QueryUniqueIndexesAsync(ctx, "FileKeys");
        uniqueIndexes.Should().Contain("IX_FileKeys_FileVersionId");
    }

    [Fact]
    public async Task TC005_OpenIddict_tables_exist_with_expected_unique_indexes()
    {
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        // ClientId uniqueness is how the token endpoint looks up registrations — pinned by
        // OpenIddictSeedWorker tests already, but this asserts the DB layer carries the invariant.
        var appIndexes = await QueryUniqueIndexesAsync(ctx, "OpenIddictApplications");
        appIndexes.Should().Contain("IX_OpenIddictApplications_ClientId");

        var scopeIndexes = await QueryUniqueIndexesAsync(ctx, "OpenIddictScopes");
        scopeIndexes.Should().Contain("IX_OpenIddictScopes_Name");

        // Reference-token lookup uniqueness — without this, a collision on ReferenceId would let
        // two tokens resolve to the same row.
        var tokenIndexes = await QueryUniqueIndexesAsync(ctx, "OpenIddictTokens");
        tokenIndexes.Should().Contain("IX_OpenIddictTokens_ReferenceId");
    }

    [Fact]
    public async Task MassTransitOutbox_migration_creates_expected_tables_and_indexes()
    {
        // STRG-061 foundation: MassTransit send-side outbox requires InboxState / OutboxState /
        // OutboxMessage. If a future EF model change drops AddInboxStateEntity() et al. from
        // OnModelCreating, the migration would skip these tables — and the outbox would silently
        // turn into a direct-publish with no dual-write protection. Pin the tables and the hot
        // indexes the dispatcher relies on.
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        var tables = await QueryTableNamesAsync(ctx);
        tables.Should().Contain(new[] { "InboxState", "OutboxState", "OutboxMessage" });

        // IX_OutboxMessage_EnqueueTime is the working-set index the BusOutboxDeliveryService
        // polling query orders/filters by. Without it, polling does a seq scan over every row
        // the outbox has ever held, including Delivered ones awaiting DuplicateDetectionWindow
        // expiry. Pinning it keeps that regression loud.
        var outboxIndexes = await QueryIndexNamesAsync(ctx, "OutboxMessage");
        outboxIndexes.Should().Contain("IX_OutboxMessage_EnqueueTime");
    }

    [Fact]
    public async Task Migration_roundtrip_Down_then_Up_restores_schema()
    {
        // Task #74 trigger: second migration (MassTransitOutbox) exists, so the Down-then-Up
        // roundtrip is now observable. Reversibility matters for the operator deploy story —
        // a failed rollout must be able to `dotnet ef database update <previous>` without data
        // loss of the tables that existed at the previous migration. This test pins that
        // contract: MassTransitOutbox.Down restores the post-InitialCreate shape, and a fresh
        // forward apply lands back in the same place.
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        var fullTables = await QueryTableNamesAsync(ctx);
        fullTables.Should().Contain("OutboxMessage");

        var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("InitialCreate");

        var postDownTables = await QueryTableNamesAsync(ctx);
        postDownTables.Should().NotContain("OutboxMessage", "Down must drop the outbox tables");
        postDownTables.Should().NotContain("InboxState");
        postDownTables.Should().NotContain("OutboxState");
        postDownTables.Should().Contain("Users",
            "InitialCreate tables must survive the partial rollback — only MassTransitOutbox is reversed");

        // Forward apply again: schema must converge to the original HEAD shape.
        await migrator.MigrateAsync();
        var postUpTables = await QueryTableNamesAsync(ctx);
        postUpTables.Should().BeEquivalentTo(fullTables,
            "migrating forward after a Down must produce the same schema as the original Up");
    }

    [Fact]
    public async Task TC007_Drives_ProviderConfig_rejects_values_over_8192_chars()
    {
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        // Defense-in-depth on admin-set ProviderConfig JSON (task #56 / STRG-043 L3).
        // The DB column is varchar(8192); a raw INSERT with 8193 chars must fail with SqlState
        // 22001 (string_data_right_truncation). If a future change drops the length cap, this
        // test makes the regression loud.
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        var oversized = new string('x', 8193);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "Drives"
                ("Id", "TenantId", "Name", "ProviderType", "ProviderConfig", "VersioningPolicy",
                 "EncryptionEnabled", "IsDefault", "CreatedAt", "UpdatedAt", "DeletedAt")
            VALUES
                (@id, @tenantId, 'd', 'local', @cfg, '{}', false, false,
                 NOW(), NOW(), NULL)
            """;
        AddParam(cmd, "id", Guid.NewGuid());
        AddParam(cmd, "tenantId", Guid.NewGuid());
        AddParam(cmd, "cfg", oversized);

        var act = async () => await cmd.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("22001",
            "Drives.ProviderConfig is capped at varchar(8192) as admin-hardening defense-in-depth");

        static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
    }

    [Fact]
    public async Task TC006_Tags_ValueType_check_constraint_rejects_invalid_values()
    {
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        // Exercise the check constraint via a raw INSERT through the existing EF connection.
        // Reusing ctx.Database.GetDbConnection() avoids the Npgsql "password stripped after
        // first Open" gotcha that would otherwise surface if we constructed a new NpgsqlConnection
        // from the post-open connection string.
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "Tags"
                ("Id", "FileId", "UserId", "Key", "Value", "ValueType", "TenantId",
                 "CreatedAt", "UpdatedAt", "DeletedAt")
            VALUES
                (@id, @fileId, @userId, 'project', '42', 'bogus', @tenantId,
                 NOW(), NOW(), NULL)
            """;
        AddParam(cmd, "id", Guid.NewGuid());
        AddParam(cmd, "fileId", Guid.NewGuid());
        AddParam(cmd, "userId", Guid.NewGuid());
        AddParam(cmd, "tenantId", Guid.NewGuid());

        var act = async () => await cmd.ExecuteNonQueryAsync();

        // Postgres surfaces check-constraint violations as SqlState 23514.
        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514",
            "the CK_Tags_ValueType constraint must reject values outside ('string', 'number', 'boolean')");

        static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
    }

    [Fact]
    public async Task Migration_pins_tenant_scoped_unique_indexes()
    {
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        // IX_Users_TenantId_Email: DB-layer defence against the concurrent-registration
        // account-shadowing race. Two simultaneous /register POSTs for the same (tenant, email)
        // can both pass the app-layer "email taken?" check in UserManager before either has
        // committed; only the unique index serialises the conflict. Without it, both inserts
        // land and subsequent login becomes nondeterministic across the two rows.
        var userIndexes = await QueryUniqueIndexesAsync(ctx, "Users");
        userIndexes.Should().Contain("IX_Users_TenantId_Email");
        var userColumns = await QueryIndexColumnOrderAsync(ctx, "IX_Users_TenantId_Email");
        userColumns.Should().Equal("TenantId", "Email");

        // IX_Drives_TenantId_Name: backs DuplicateDriveNameException. The service-layer
        // pre-check is necessary for a clean error shape but insufficient under concurrency —
        // this index is the actual serialisation point.
        var driveIndexes = await QueryUniqueIndexesAsync(ctx, "Drives");
        driveIndexes.Should().Contain("IX_Drives_TenantId_Name");
        var driveColumns = await QueryIndexColumnOrderAsync(ctx, "IX_Drives_TenantId_Name");
        driveColumns.Should().Equal("TenantId", "Name");

        // IX_Tags_FileId_UserId_Key: backs TagService.UpsertAsync. Downgrading this to
        // non-unique would silently turn Upsert into Insert — every edit of an existing tag
        // would create a duplicate row instead of updating, and ListAsync would start
        // returning the same (key, value) twice.
        var tagIndexes = await QueryUniqueIndexesAsync(ctx, "Tags");
        tagIndexes.Should().Contain("IX_Tags_FileId_UserId_Key");
        var tagColumns = await QueryIndexColumnOrderAsync(ctx, "IX_Tags_FileId_UserId_Key");
        tagColumns.Should().Equal("FileId", "UserId", "Key");

        // IX_AuditEntries_EventId: partial unique index over the MassTransit MessageId.
        // AuditLogConsumer.IsEventIdUniqueViolation now equality-matches this exact name
        // (STRG-062 INFO-2 follow-up). A future "clarity rename" to UQ_AuditEntries_Idempotency
        // or IX_AuditEntries_MessageId would let the consumer rethrow every duplicate event →
        // retry pipeline → DLQ storm after a routine migration. Pin the name here so the
        // triangulation (config pin + consumer equality + schema pin) fails loud instead.
        var auditIndexes = await QueryUniqueIndexesAsync(ctx, "AuditEntries");
        auditIndexes.Should().Contain("IX_AuditEntries_EventId");

        // IX_Notifications_EventId: the Notifications-side twin. Same failure mode, same
        // triangulation — QuotaNotificationConsumer.IsDuplicateEventId equality-matches this
        // exact name (STRG-064 INFO-4 follow-up, folded into the #85 scope). Renaming either
        // index without updating the shared const would fail the migration pin below.
        var notificationIndexes = await QueryUniqueIndexesAsync(ctx, "Notifications");
        notificationIndexes.Should().Contain("IX_Notifications_EventId");
    }

    [Fact]
    public async Task Migration_pins_FileKey_cascade_delete()
    {
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        // Refactor risks this catches:
        //  - Silent downgrade to SetNull: PruneVersionsAsync succeeds but DEKs stay behind
        //    forever, pointing at FileVersion rows that no longer exist. A later key-rotation
        //    job that iterates FileKeys would trip on the dangling FileVersionId.
        //  - Silent downgrade to Restrict: every FileVersion delete fails with an FK violation,
        //    breaking the per-version atomic loop in FileVersionStore.PruneVersionsAsync.
        // Both regressions compile, pass unit tests, and only surface in integration flows —
        // hence pinning at the migration layer.
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT con.confdeltype
            FROM pg_constraint con
            JOIN pg_class rel ON con.conrelid = rel.oid
            JOIN pg_attribute att ON att.attrelid = rel.oid AND att.attnum = ANY(con.conkey)
            WHERE rel.relname = 'FileKeys'
              AND att.attname = 'FileVersionId'
              AND con.contype = 'f'
            """;

        var result = await cmd.ExecuteScalarAsync();
        result.Should().NotBeNull("FileKeys.FileVersionId must have a foreign-key constraint");
        // pg_constraint.confdeltype is the internal "char" type — Npgsql maps it to System.Char.
        // 'c' = CASCADE, 'n' = SET NULL, 'r' = RESTRICT, 'a' = NO ACTION, 'd' = SET DEFAULT.
        ((char)result!).Should().Be('c', "ON DELETE CASCADE is confdeltype='c' in pg_constraint");
    }

    [Fact]
    public async Task Migration_pins_PasswordHash_unbounded_text_type()
    {
        await using var ctx = await BuildContextForFreshDatabaseAsync();
        await ctx.Database.MigrateAsync();

        // Refactor risk: a "consistency" PR caps PasswordHash to varchar(N) to match Email.
        // A subsequent PBKDF2 iteration-bump produces a longer hash encoding — Postgres
        // silently truncates on insert, the stored hash no longer matches the full computed
        // hash on verify, and every post-bump login fails. The failure mode reads as "mass
        // auth outage" but its root cause is a column-width regression several PRs upstream.
        // Pinning 'text' here is the canary.
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'Users'
              AND column_name = 'PasswordHash'
            """;

        var dataType = (string?)await cmd.ExecuteScalarAsync();
        dataType.Should().Be("text");
    }

    private async Task<StrgDbContext> BuildContextForFreshDatabaseAsync(Guid? tenantId = null)
    {
        var dbName = $"strg_migration_test_{Guid.NewGuid():N}";
        var adminConnectionString = _postgres.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var testConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = dbName,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<StrgDbContext>()
            .UseNpgsql(testConnectionString)
            .UseOpenIddict()
            .Options;

        return new StrgDbContext(options, new FixedTenantContext(tenantId ?? Guid.NewGuid()));
    }

    private static async Task<HashSet<string>> QueryTableNamesAsync(DbContext ctx)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public'
            """;

        var results = new HashSet<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    private static async Task<HashSet<string>> QueryColumnsAsync(DbContext ctx, string tableName)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @tableName
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "tableName";
        param.Value = tableName;
        cmd.Parameters.Add(param);

        var results = new HashSet<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    private static async Task<HashSet<string>> QueryUniqueIndexesAsync(DbContext ctx, string tableName)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.relname
            FROM pg_class c
            JOIN pg_index ix ON c.oid = ix.indrelid
            JOIN pg_class i ON ix.indexrelid = i.oid
            WHERE c.relname = @tableName AND ix.indisunique = true
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "tableName";
        param.Value = tableName;
        cmd.Parameters.Add(param);

        var results = new HashSet<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    private static async Task<HashSet<string>> QueryIndexNamesAsync(DbContext ctx, string tableName)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.relname
            FROM pg_class c
            JOIN pg_index ix ON c.oid = ix.indrelid
            JOIN pg_class i ON ix.indexrelid = i.oid
            WHERE c.relname = @tableName
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "tableName";
        param.Value = tableName;
        cmd.Parameters.Add(param);

        var results = new HashSet<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    private static async Task<IReadOnlyList<string>> QueryIndexColumnOrderAsync(DbContext ctx, string indexName)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        // pg_index.indkey is an int2vector of attribute numbers in declared order. generate_subscripts
        // walks the vector by index position (1-based); ORDER BY s preserves that declared order in
        // the result set. Column order in a composite index is load-bearing: IX_Users_TenantId_Email
        // and IX_Users_Email_TenantId both enforce uniqueness, but only the first supports tenant-
        // scoped range scans on TenantId as the leading column.
        cmd.CommandText = """
            SELECT a.attname
            FROM pg_index ix
            JOIN pg_class i ON ix.indexrelid = i.oid
            CROSS JOIN generate_subscripts(ix.indkey, 1) AS s
            JOIN pg_attribute a ON a.attrelid = ix.indrelid AND a.attnum = ix.indkey[s]
            WHERE i.relname = @indexName
            ORDER BY s
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "indexName";
        param.Value = indexName;
        cmd.Parameters.Add(param);

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }
}
