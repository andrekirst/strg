using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Identity;

/// <summary>
/// Bootstraps the database on first launch by creating a default tenant and a SuperAdmin user
/// whenever the <c>users</c> table is empty. Idempotent across multi-replica startups via a
/// process-wide PostgreSQL advisory lock, so exactly one replica performs the seed and prints
/// the generated admin password to <see cref="Console.Out"/>.
///
/// <para><b>Stdout delivery is not log-free.</b> The generated password is written to
/// <see cref="Console.Out"/>, which any realistic deployment captures: Docker's json-file /
/// journald log drivers, Kubernetes' kubelet, systemd's journald, and common sidecars
/// (Fluent Bit, Vector, Datadog). The message to the operator acknowledges this — a prior
/// revision claimed the password was "NOT persisted to logs," which was false and could cause
/// an operator to skip the post-bootstrap log-scrub step. The v0.2 backlog covers optional
/// persistence to <c>/var/lib/strg/first-run-password</c> (mode 0600) and
/// <c>STRG_INITIAL_ADMIN_PASSWORD</c> env-var injection to skip generation entirely; this
/// revision ships the honest warning only.</para>
/// </summary>
public sealed class FirstRunInitializationService(IServiceProvider services) : IHostedService
{
    private const string SuperAdminEmail = "admin@strg.local";
    private const string SuperAdminDisplayName = "Super Admin";
    private const string DefaultTenantName = "default";

    // 18 random bytes → 24 URL-safe base64 chars (no padding, since 18 is divisible by 3).
    // ≈ 144 bits of entropy — copy-paste safe through docker logs and shell quoting.
    private const int PasswordEntropyBytes = 18;

    // Project-specific advisory lock key: a stable bigint so all replicas contend for the same
    // lock. Value has no semantic meaning beyond "unlikely to collide with other subsystems".
    private const long AdvisoryLockKey = 7390023145001L;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var isPostgres = db.Database.IsNpgsql();

        // Advisory locks are session-scoped. Keep the connection open explicitly — otherwise EF
        // Core closes it between commands and the lock would release immediately.
        if (isPostgres)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            if (isPostgres)
            {
                await db.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_lock({AdvisoryLockKey})",
                    cancellationToken);
            }

            try
            {
                await SeedIfEmptyAsync(db, passwordHasher, cancellationToken);
            }
            finally
            {
                if (isPostgres)
                {
                    // Use CancellationToken.None so a shutdown mid-seed still releases the lock
                    // promptly. (Session-scoped locks also release on connection close as a
                    // safety net.)
                    await db.Database.ExecuteSqlAsync(
                        $"SELECT pg_advisory_unlock({AdvisoryLockKey})",
                        CancellationToken.None);
                }
            }
        }
        finally
        {
            if (isPostgres)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task SeedIfEmptyAsync(
        StrgDbContext db,
        IPasswordHasher passwordHasher,
        CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: there is no tenant context at startup, so the default filter would
        // scope to Guid.Empty and hide existing users in real tenants.
        var hasAnyUser = await db.Users.IgnoreQueryFilters().AnyAsync(cancellationToken);
        if (hasAnyUser)
        {
            return;
        }

        var tenant = new Tenant { Name = DefaultTenantName };
        db.Tenants.Add(tenant);

        var password = GeneratePassword();
        var admin = new User
        {
            TenantId = tenant.Id,
            Email = SuperAdminEmail,
            DisplayName = SuperAdminDisplayName,
            PasswordHash = passwordHasher.Hash(password),
            Role = UserRole.SuperAdmin,
        };
        db.Users.Add(admin);

        await db.SaveChangesAsync(cancellationToken);

        PrintInitialPassword(password);
    }

    private static string GeneratePassword()
    {
        // URL-safe base64: [A-Za-z0-9_-]. Survives shell quoting and log scrapers.
        var bytes = RandomNumberGenerator.GetBytes(PasswordEntropyBytes);
        return Base64Url.EncodeToString(bytes);
    }

    private static void PrintInitialPassword(string password)
    {
        // Write directly to Console.Out to bypass ILogger — Serilog sinks (file, OTLP, Seq)
        // must not see this secret via the structured-logging pipeline. Note this does NOT mean
        // the password stays local: stdout is captured by Docker / Kubernetes / journald and most
        // sidecar log shippers. The warning below tells the operator the truth.
        //
        // RS1035 flags Console use project-wide (EnforceExtendedAnalyzerRules in Directory.Build.props),
        // but that rule targets Roslyn analyzer assemblies; this is runtime host startup code.
#pragma warning disable RS1035
        const string bar = "=========================================================================";
        Console.Out.WriteLine(bar);
        Console.Out.WriteLine("  strg first-run initialization: SuperAdmin account created");
        Console.Out.WriteLine($"  Email:    {SuperAdminEmail}");
        Console.Out.WriteLine($"  Password: {password}");
        Console.Out.WriteLine("  WARNING: this is the ONLY time this password is shown.");
        Console.Out.WriteLine("  stdout is typically captured by container runtimes (docker logs,");
        Console.Out.WriteLine("  kubectl logs, journald) and by log-shipping sidecars. Copy the");
        Console.Out.WriteLine("  password to a secrets manager NOW, scrub the bootstrap log");
        Console.Out.WriteLine("  stream, then rotate the password after first login.");
        Console.Out.WriteLine(bar);
        Console.Out.Flush();
#pragma warning restore RS1035
    }
}
