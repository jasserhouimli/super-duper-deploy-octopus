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
}
