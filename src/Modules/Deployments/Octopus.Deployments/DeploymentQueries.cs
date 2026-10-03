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
}
