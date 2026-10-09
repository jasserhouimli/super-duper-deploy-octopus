using Microsoft.EntityFrameworkCore;

namespace Octopus.Deployments;

/// <summary>
/// Lease rules for multi-worker claims. A deployment mid-flight (Cloning,
/// Building, Starting) is recoverable only when its lease is missing or
/// expired — a live lease means another worker is actively working it, so a
/// restarting worker must not steal it. Heartbeats renew only their own lease.
/// </summary>
public static class DeploymentLeases
{
    public static bool IsActive(Deployment deployment) =>
        deployment.Status is DeploymentStatus.Cloning or DeploymentStatus.Building or DeploymentStatus.Starting;

    public static bool IsStale(Deployment deployment, DateTimeOffset now) =>
        IsActive(deployment) && (deployment.LeaseExpiresAt is null || deployment.LeaseExpiresAt <= now);

    /// <summary>Active deployments whose lease lapsed (bounded prefetch; sorted by the caller if needed).</summary>
    public static async Task<List<Deployment>> ListRecoverableAsync(
        OctopusDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        // NOTE: SQLite cannot compare DateTimeOffset server-side reliably, so
        // prefetch active rows (bounded) and apply the lease check in memory.
        var rows = await db.Deployments.Where(d =>
            d.Status == DeploymentStatus.Cloning || d.Status == DeploymentStatus.Building || d.Status == DeploymentStatus.Starting)
            .Take(1000).ToListAsync(ct);
        return rows.Where(d => d.LeaseExpiresAt is null || d.LeaseExpiresAt <= now).ToList();
    }

    /// <summary>Extends the lease when this worker still owns it. False = lost or stolen, stop renewing.</summary>
    public static bool TryRenew(Deployment deployment, string owner, DateTimeOffset now, TimeSpan duration)
    {
        if (!string.Equals(deployment.LeaseOwner, owner, StringComparison.Ordinal))
            return false;
        deployment.LeaseExpiresAt = now.Add(duration);
        return true;
    }

    /// <summary>
    /// Graceful-shutdown handoff: clears this worker's active leases so a
    /// restart (or peer) can recover the work immediately instead of waiting
    /// for the lease to lapse. Returns the number of released deployments.
    /// </summary>
    public static async Task<int> ReleaseOwnedAsync(
        OctopusDbContext db, string owner, CancellationToken ct = default) =>
        await db.Deployments.Where(d =>
            (d.Status == DeploymentStatus.Cloning || d.Status == DeploymentStatus.Building || d.Status == DeploymentStatus.Starting)
            && d.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.LeaseOwner, (string?)null)
                .SetProperty(d => d.LeaseExpiresAt, (DateTimeOffset?)null), ct);
}
