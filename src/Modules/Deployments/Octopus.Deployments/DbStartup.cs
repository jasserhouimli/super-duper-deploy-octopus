using Microsoft.EntityFrameworkCore;

namespace Octopus.Deployments;

/// <summary>
/// Startup schema convergence. PostgreSQL applies EF migrations (the source of
/// truth for new environments); SQLite keeps EnsureCreated + DbBootstrap for
/// zero-setup dev and in-place upgrades of existing files.
/// </summary>
public static class DbStartup
{
    public static bool IsPostgres(OctopusDbContext db) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    public static async Task EnsureReadyAsync(OctopusDbContext db, CancellationToken ct = default)
    {
        if (IsPostgres(db))
            await db.Database.MigrateAsync(ct);
        else
            DbBootstrap.EnsureUpgraded(db);
    }
}
