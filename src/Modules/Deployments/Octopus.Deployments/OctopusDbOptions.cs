using Microsoft.EntityFrameworkCore;

namespace Octopus.Deployments;

/// <summary>
/// Control-plane database wiring. SQLite is the default (zero-setup file);
/// any connection string with `Host=` selects PostgreSQL via Npgsql.
/// Postgres currently runs on EnsureCreated like SQLite — real EF migrations
/// are the next persistence step (see roadmap).
/// </summary>
public static class OctopusDbOptions
{
    public static void Configure(DbContextOptionsBuilder<OctopusDbContext> builder, string? connectionString)
    {
        var cs = string.IsNullOrWhiteSpace(connectionString) ? "Data Source=octopus.db" : connectionString;
        if (IsPostgres(cs))
            builder.UseNpgsql(cs);
        else
            builder.UseSqlite(cs);
    }

    internal static bool IsPostgres(string connectionString) =>
        connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase);
}
