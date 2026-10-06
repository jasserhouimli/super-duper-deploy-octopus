using Microsoft.EntityFrameworkCore;
using Octopus.Deployments.Webhooks;

namespace Octopus.Deployments;

/// <summary>
/// Read queries for deployments and webhook receipts.
/// SQLite cannot ORDER BY DateTimeOffset server-side, so these prefetch a
/// bounded window with translatable predicates and sort in memory.
/// Covered by Sqlite-backed tests (see SqliteOrderingTests).
/// </summary>
public static class DeploymentQueries
{
    public static async Task<List<Deployment>> ListByAppAsync(
        OctopusDbContext db, Guid appId, int max = 50, CancellationToken ct = default)
    {
        var rows = await db.Deployments.Where(d => d.AppId == appId).Take(1000).ToListAsync(ct);
        return rows.OrderByDescending(d => d.CreatedAt).Take(Math.Clamp(max, 1, 100)).ToList();
    }

    public static async Task<List<WebhookEvent>> ListWebhookEventsAsync(
        OctopusDbContext db, Guid appId, int max = 50, CancellationToken ct = default)
    {
        var rows = await db.WebhookEvents.Where(e => e.AppId == appId).Take(5000).ToListAsync(ct);
        return rows.OrderByDescending(e => e.ReceivedAt).Take(Math.Clamp(max, 1, 200)).ToList();
    }

    /// <summary>
    /// Ascending log tail for polling/streaming. `afterId` is an exclusive
    /// cursor (0 = from the start); `take` is clamped to keep queries bounded.
    /// Ordering by the integer PK is server-side and SQLite-safe.
    /// </summary>
    public static async Task<List<DeploymentLog>> ListLogsAsync(
        OctopusDbContext db, Guid deploymentId, long afterId = 0, int take = 200, CancellationToken ct = default)
    {
        var n = Math.Clamp(take, 1, 1000);
        return await db.DeploymentLogs
            .Where(l => l.DeploymentId == deploymentId && l.Id > afterId)
            .OrderBy(l => l.Id)
            .Take(n)
            .ToListAsync(ct);
    }
}
