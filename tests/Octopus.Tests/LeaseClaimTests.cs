using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;

namespace Octopus.Tests;

public sealed class LeaseClaimTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private Guid _deploymentId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options;
        await using var db = new OctopusDbContext(options);
        DbBootstrap.EnsureUpgraded(db);
        _deploymentId = Guid.NewGuid();
        db.Deployments.Add(new Deployment { Id = _deploymentId, AppId = Guid.NewGuid(), Status = DeploymentStatus.Queued });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _conn.DisposeAsync();

    private OctopusDbContext NewContext() =>
        new(new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options);

    [Fact]
    public async Task Concurrent_claims_resolve_to_one_winner()
    {
        await using var ctxA = NewContext();
        await using var ctxB = NewContext();
        var a = await ctxA.Deployments.FindAsync([_deploymentId]);
        var b = await ctxB.Deployments.FindAsync([_deploymentId]);

        a!.Status = DeploymentStatus.Cloning;
        a.LeaseOwner = "worker-a";
        a.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await ctxA.SaveChangesAsync();

        b!.Status = DeploymentStatus.Cloning;
        b.LeaseOwner = "worker-b";
        b.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());

        await using var check = NewContext();
        var row = await check.Deployments.FindAsync([_deploymentId]);
        Assert.Equal("worker-a", row!.LeaseOwner);
        Assert.Equal(DeploymentStatus.Cloning, row.Status);
    }

    [Fact]
    public async Task Lease_columns_upgrade_existing_rows_to_null()
    {
        await using var db = NewContext();
        var row = await db.Deployments.FindAsync([_deploymentId]);
        Assert.Null(row!.LeaseOwner);
        Assert.Null(row.LeaseExpiresAt);
    }
}
