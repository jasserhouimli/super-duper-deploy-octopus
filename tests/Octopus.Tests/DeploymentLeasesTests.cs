using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;

namespace Octopus.Tests;

public sealed class DeploymentLeasesTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private OctopusDbContext _db = null!;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

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

    [Theory]
    [InlineData(DeploymentStatus.Cloning)]
    [InlineData(DeploymentStatus.Building)]
    [InlineData(DeploymentStatus.Starting)]
    public void Active_with_expired_or_missing_lease_is_stale(DeploymentStatus status)
    {
        Assert.True(DeploymentLeases.IsStale(new Deployment { Status = status, LeaseExpiresAt = _now.AddMinutes(-1) }, _now));
        Assert.True(DeploymentLeases.IsStale(new Deployment { Status = status, LeaseExpiresAt = null }, _now));
    }

    [Theory]
    [InlineData(DeploymentStatus.Cloning)]
    [InlineData(DeploymentStatus.Building)]
    [InlineData(DeploymentStatus.Starting)]
    public void Active_with_live_lease_is_not_stale(DeploymentStatus status)
    {
        Assert.False(DeploymentLeases.IsStale(
            new Deployment { Status = status, LeaseOwner = "w1", LeaseExpiresAt = _now.AddMinutes(2) }, _now));
    }

    [Theory]
    [InlineData(DeploymentStatus.Queued)]
    [InlineData(DeploymentStatus.Running)]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Stopped)]
    [InlineData(DeploymentStatus.Cancelled)]
    public void Non_active_is_never_stale(DeploymentStatus status)
    {
        Assert.False(DeploymentLeases.IsStale(
            new Deployment { Status = status, LeaseExpiresAt = _now.AddMinutes(-10) }, _now));
    }

    [Fact]
    public void TryRenew_extends_only_own_lease()
    {
        var d = new Deployment { Status = DeploymentStatus.Building, LeaseOwner = "w1" };
        Assert.True(DeploymentLeases.TryRenew(d, "w1", _now, TimeSpan.FromMinutes(2)));
        Assert.Equal(_now.AddMinutes(2), d.LeaseExpiresAt);

        var stolen = new Deployment { Status = DeploymentStatus.Building, LeaseOwner = "w2", LeaseExpiresAt = _now };
        Assert.False(DeploymentLeases.TryRenew(stolen, "w1", _now, TimeSpan.FromMinutes(2)));
        Assert.Equal(_now, stolen.LeaseExpiresAt);
    }

    [Fact]
    public async Task ListRecoverable_returns_only_lapsed_active()
    {
        var live = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = DeploymentStatus.Building, LeaseOwner = "w1", LeaseExpiresAt = _now.AddMinutes(2) };
        var expired = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = DeploymentStatus.Cloning, LeaseOwner = "w1", LeaseExpiresAt = _now.AddMinutes(-1) };
        var noLease = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = DeploymentStatus.Starting, LeaseExpiresAt = null };
        var queued = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = DeploymentStatus.Queued, LeaseExpiresAt = null };
        _db.Deployments.AddRange(live, expired, noLease, queued);
        await _db.SaveChangesAsync();

        var found = await DeploymentLeases.ListRecoverableAsync(_db, _now);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, d => d.Id == expired.Id);
        Assert.Contains(found, d => d.Id == noLease.Id);
    }

    [Fact]
    public async Task ReleaseOwned_clears_only_own_active_leases()
    {
        var mine = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = DeploymentStatus.Building, LeaseOwner = "w1", LeaseExpiresAt = _now.AddMinutes(2) };
        var peer = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = DeploymentStatus.Cloning, LeaseOwner = "w2", LeaseExpiresAt = _now.AddMinutes(2) };
        var idle = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = DeploymentStatus.Queued, LeaseOwner = "w1" };
        _db.Deployments.AddRange(mine, peer, idle);
        await _db.SaveChangesAsync();

        Assert.Equal(1, await DeploymentLeases.ReleaseOwnedAsync(_db, "w1"));

        await using var check = new OctopusDbContext(
            new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options);
        var mineRow = await check.Deployments.FindAsync([mine.Id]);
        Assert.Null(mineRow!.LeaseOwner);
        Assert.Null(mineRow.LeaseExpiresAt);
        Assert.Equal(DeploymentStatus.Building, mineRow.Status); // status untouched: recovery decides
        var peerRow = await check.Deployments.FindAsync([peer.Id]);
        Assert.Equal("w2", peerRow!.LeaseOwner);
    }
}
