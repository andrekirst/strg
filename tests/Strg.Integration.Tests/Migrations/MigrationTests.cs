using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
}
