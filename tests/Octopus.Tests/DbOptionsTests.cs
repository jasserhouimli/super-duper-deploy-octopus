using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;

namespace Octopus.Tests;

public sealed class DbOptionsTests
{
    [Theory]
    [InlineData("Host=db;Username=octopus;Password=x;Database=octopus", true)]
    [InlineData("host=localhost;database=o", true)]
    [InlineData("Data Source=octopus.db", false)]
    [InlineData("DataSource=:memory:", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Selects_provider_by_connection_string(string? cs, bool postgres)
    {
        var builder = new DbContextOptionsBuilder<OctopusDbContext>();
        OctopusDbOptions.Configure(builder, cs);
        Assert.Equal(postgres, builder.Options.Extensions.Any(e => e.GetType().FullName!.Contains("Npgsql")));
    }

    [Fact]
    public void Startup_detects_provider_without_connecting()
    {
        using var pg = new OctopusDbContext(
            new DbContextOptionsBuilder<OctopusDbContext>().UseNpgsql("Host=db").Options);
        using var lite = new OctopusDbContext(
            new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite("DataSource=:memory:").Options);
        Assert.True(DbStartup.IsPostgres(pg));
        Assert.False(DbStartup.IsPostgres(lite));
    }

    [Fact]
    public async Task Startup_readies_sqlite_schema()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        using var db = new OctopusDbContext(
            new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(conn).Options);
        await DbStartup.EnsureReadyAsync(db);
        Assert.True(await db.Database.CanConnectAsync());
        Assert.True((await db.Database.GetAppliedMigrationsAsync()).Any() == false); // SQLite: bootstrap, not migrations
    }
}
