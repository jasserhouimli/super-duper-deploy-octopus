using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;

namespace Octopus.Tests;

public sealed class DeploymentLogQueryTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private OctopusDbContext _db = null!;
    private Guid _deploymentId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _db = new OctopusDbContext(new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options);
        DbBootstrap.EnsureUpgraded(_db);
        _deploymentId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            _db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = _deploymentId, Line = $"line {i}" });
        _db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = Guid.NewGuid(), Line = "other deployment" });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Tail_is_ascending_and_scoped_to_deployment()
    {
        var logs = await DeploymentQueries.ListLogsAsync(_db, _deploymentId);
        Assert.Equal(5, logs.Count);
        Assert.Equal("line 0", logs[0].Line);
        Assert.Equal("line 4", logs[4].Line);
        Assert.True(logs[0].Id < logs[1].Id);
    }

    [Fact]
    public async Task Cursor_resumes_after_last_seen_id()
    {
        var first = await DeploymentQueries.ListLogsAsync(_db, _deploymentId, take: 2);
        Assert.Equal(2, first.Count);
        var rest = await DeploymentQueries.ListLogsAsync(_db, _deploymentId, afterId: first[^1].Id);
        Assert.Equal(3, rest.Count);
        Assert.Equal("line 2", rest[0].Line);
    }

    [Fact]
    public async Task Take_is_clamped_and_empty_for_unknown_deployment()
    {
        var logs = await DeploymentQueries.ListLogsAsync(_db, _deploymentId, take: 10_000);
        Assert.Equal(5, logs.Count);
        Assert.Empty(await DeploymentQueries.ListLogsAsync(_db, Guid.NewGuid()));
    }

    [Fact]
    public async Task AppExists_reflects_present_and_missing_apps()
    {
        var id = Guid.NewGuid();
        Assert.False(await DeploymentQueries.AppExistsAsync(_db, id));
        _db.Apps.Add(new Octopus.Apps.App { Id = id, Name = "x", Slug = "x", RepoUrl = "https://github.com/o/r.git", Branch = "main" });
        await _db.SaveChangesAsync();
        Assert.True(await DeploymentQueries.AppExistsAsync(_db, id));
    }
}

public sealed class DeploymentPruneTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private OctopusDbContext _db = null!;
    private Guid _appId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _db = new OctopusDbContext(new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options);
        DbBootstrap.EnsureUpgraded(_db);
        _appId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 55; i++)
        {
            var id = Guid.NewGuid();
            _db.Deployments.Add(new Deployment
            {
                Id = id,
                AppId = _appId,
                Status = DeploymentStatus.Running,
                CreatedAt = now.AddMinutes(-i),
            });
            _db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = id, Line = $"log {i}" });
        }
        var other = Guid.NewGuid();
        _db.Deployments.Add(new Deployment { Id = Guid.NewGuid(), AppId = other, CreatedAt = now });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Prune_keeps_newest_and_removes_their_logs()
    {
        Assert.Equal(5, await DeploymentQueries.PruneAsync(_db, _appId));
        Assert.Equal(50, await _db.Deployments.CountAsync(d => d.AppId == _appId));
        Assert.Equal(50, await _db.DeploymentLogs.CountAsync());
        Assert.Equal(0, await DeploymentQueries.PruneAsync(_db, _appId));
    }

    [Fact]
    public async Task Prune_leaves_other_apps_alone()
    {
        await DeploymentQueries.PruneAsync(_db, Guid.NewGuid());
        Assert.Equal(55, await _db.Deployments.CountAsync(d => d.AppId == _appId));
    }
}
