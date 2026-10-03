using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;
using Octopus.Deployments.Webhooks;

namespace Octopus.Tests;

/// <summary>
/// Regression tests: SQLite cannot ORDER BY DateTimeOffset server-side, so all
/// list queries must work through these helpers against a real SQLite database.
/// </summary>
public sealed class SqliteOrderingTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private OctopusDbContext _db = null!;
    private Guid _appId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _db = new OctopusDbContext(new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options);
        await _db.Database.EnsureCreatedAsync();

        _appId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _db.Deployments.AddRange(
            new Deployment { Id = Guid.NewGuid(), AppId = _appId, CreatedAt = now.AddMinutes(-2) },
            new Deployment { Id = Guid.NewGuid(), AppId = _appId, CreatedAt = now },
            new Deployment { Id = Guid.NewGuid(), AppId = _appId, CreatedAt = now.AddMinutes(-1) });
        _db.WebhookEvents.AddRange(
            new WebhookEvent { Id = Guid.NewGuid(), AppId = _appId, DeliveryId = "a", EventType = "push", PayloadHash = "h", ReceivedAt = now.AddMinutes(-1) },
            new WebhookEvent { Id = Guid.NewGuid(), AppId = _appId, DeliveryId = "b", EventType = "push", PayloadHash = "h", ReceivedAt = now });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Deployments_list_newest_first()
    {
        var list = await DeploymentQueries.ListByAppAsync(_db, _appId);
        Assert.Equal(3, list.Count);
        Assert.True(list[0].CreatedAt >= list[1].CreatedAt && list[1].CreatedAt >= list[2].CreatedAt);
    }

    [Fact]
    public async Task Webhook_events_list_newest_first()
    {
        var list = await DeploymentQueries.ListWebhookEventsAsync(_db, _appId);
        Assert.Equal(2, list.Count);
        Assert.Equal("b", list[0].DeliveryId);
    }
}
