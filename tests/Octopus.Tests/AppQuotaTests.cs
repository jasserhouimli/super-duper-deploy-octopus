using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Apps;
using Octopus.Deployments;

namespace Octopus.Tests;

public sealed class AppQuotaTests
{
    [Fact]
    public void Defaults_apply_when_null()
    {
        var r = AppQuotas.Validate(null, null);
        Assert.True(r.IsSuccess);
        Assert.Equal((512, 1000), r.Value);
    }

    [Theory]
    [InlineData(128, 250)]
    [InlineData(256, 500)]
    [InlineData(1024, 2000)]
    public void Accepts_allowlisted_quotas(int mem, int cpu) =>
        Assert.True(AppQuotas.Validate(mem, cpu).IsSuccess);

    [Theory]
    [InlineData(64, 1000)]
    [InlineData(300, 1000)]
    [InlineData(512, 100)]
    [InlineData(512, 3000)]
    [InlineData(-1, -1)]
    public void Rejects_off_list_quotas(int mem, int cpu) =>
        Assert.False(AppQuotas.Validate(mem, cpu).IsSuccess);

    [Theory]
    [InlineData(128, "128m")]
    [InlineData(2048, "2048m")]
    public void Formats_docker_memory(int mem, string expected) =>
        Assert.Equal(expected, AppQuotas.ToDockerMemory(mem));

    [Theory]
    [InlineData(250, "0.25")]
    [InlineData(500, "0.5")]
    [InlineData(1000, "1")]
    [InlineData(2000, "2")]
    public void Formats_docker_cpus(int cpu, string expected) =>
        Assert.Equal(expected, AppQuotas.ToDockerCpus(cpu));
}

public sealed class AppQuotaPersistenceTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private OctopusDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _db = new OctopusDbContext(new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options);
        DbBootstrap.EnsureUpgraded(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Quota_columns_default_and_roundtrip()
    {
        var id = Guid.NewGuid();
        _db.Apps.Add(new App { Id = id, Name = "demo", Slug = "demo", RepoUrl = "https://github.com/o/r.git", Branch = "main" });
        await _db.SaveChangesAsync();

        var fresh = await _db.Apps.FindAsync([id]);
        Assert.Equal(512, fresh!.MaxMemoryMb);
        Assert.Equal(1000, fresh.CpuMillicores);

        fresh.MaxMemoryMb = 256;
        fresh.CpuMillicores = 500;
        await _db.SaveChangesAsync();

        var reloaded = await _db.Apps.FindAsync([id]);
        Assert.Equal(256, reloaded!.MaxMemoryMb);
        Assert.Equal(500, reloaded.CpuMillicores);
    }
}
