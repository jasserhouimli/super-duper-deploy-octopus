using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Octopus.Apps;

namespace Octopus.Deployments.Webhooks;

public enum WebhookOutcome
{
    Queued,
    Duplicate,
    Ignored,
    Ping,
    AppNotFound,
    NotConfigured,
    Unauthorized,
    BadPayload,
    Conflict,
}

public sealed record WebhookResult(WebhookOutcome Outcome, Guid? DeploymentId = null, string? Message = null);

/// <summary>
/// GitHub push-webhook intake: authenticate -> persist receipt -> idempotency ->
/// branch filter -> enqueue deployment. Long execution stays in the worker;
/// this returns promptly (202/200/4xx).
/// </summary>
public static class WebhookService
{
    public static async Task<WebhookResult> HandleAsync(
        OctopusDbContext db,
        Guid appId,
        string eventType,
        string deliveryId,
        string? signature,
        string rawBody,
        CancellationToken ct)
    {
        var app = await db.Apps.FindAsync([appId], ct);
        if (app is null)
            return new WebhookResult(WebhookOutcome.AppNotFound, Message: "App not found.");

        var sub = await db.AppWebhooks.FindAsync([appId], ct);
        if (sub is null)
            return new WebhookResult(WebhookOutcome.NotConfigured, Message: "Webhook not configured for this app. POST /api/apps/{id}/webhook-token first.");

        if (string.IsNullOrWhiteSpace(deliveryId) || deliveryId.Length > 100)
            return new WebhookResult(WebhookOutcome.BadPayload, Message: "Missing X-GitHub-Delivery.");

        if (!GitHubSignature.Verify(sub.Secret, rawBody, signature))
            return new WebhookResult(WebhookOutcome.Unauthorized, Message: "Invalid X-Hub-Signature-256.");

        // Idempotency: same delivery twice (GitHub redelivery) must not enqueue twice.
        var existing = await db.WebhookEvents
            .FirstOrDefaultAsync(e => e.AppId == appId && e.DeliveryId == deliveryId, ct);
        if (existing is not null)
            return new WebhookResult(WebhookOutcome.Duplicate, existing.DeploymentId, "Delivery already processed.");

        var hash = GitHubSignature.PayloadHash(rawBody);

        if (string.Equals(eventType, "ping", StringComparison.OrdinalIgnoreCase))
        {
            db.WebhookEvents.Add(NewEvent(appId, deliveryId, eventType, hash, WebhookEventStatus.Ignored, Error: "ping"));
            await db.SaveChangesAsync(ct);
            return new WebhookResult(WebhookOutcome.Ping, Message: "pong");
        }

        if (!string.Equals(eventType, "push", StringComparison.OrdinalIgnoreCase))
        {
            db.WebhookEvents.Add(NewEvent(appId, deliveryId, eventType, hash, WebhookEventStatus.Ignored, Error: $"unsupported event '{eventType}'"));
            await db.SaveChangesAsync(ct);
            return new WebhookResult(WebhookOutcome.Ignored, Message: $"Event '{eventType}' ignored.");
        }

        string? gitRef, after;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            gitRef = root.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            after = root.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        }
        catch (JsonException)
        {
            db.WebhookEvents.Add(NewEvent(appId, deliveryId, eventType, hash, WebhookEventStatus.Received, Error: "invalid JSON"));
            await db.SaveChangesAsync(ct);
            return new WebhookResult(WebhookOutcome.BadPayload, Message: "Invalid JSON payload.");
        }

        if (string.IsNullOrEmpty(after) || after.All(c => c == '0'))
        {
            db.WebhookEvents.Add(NewEvent(appId, deliveryId, eventType, hash, WebhookEventStatus.Ignored, Ref: gitRef, Error: "branch deletion or empty push"));
            await db.SaveChangesAsync(ct);
            return new WebhookResult(WebhookOutcome.Ignored, Message: "Branch deletion ignored.");
        }

        var expectedRef = $"refs/heads/{app.Branch}";
        if (!string.Equals(gitRef, expectedRef, StringComparison.Ordinal))
        {
            db.WebhookEvents.Add(NewEvent(appId, deliveryId, eventType, hash, WebhookEventStatus.Ignored, Ref: gitRef, CommitSha: after, Error: $"tracks {expectedRef}"));
            await db.SaveChangesAsync(ct);
            return new WebhookResult(WebhookOutcome.Ignored, Message: $"Push to '{gitRef}' ignored (app tracks '{expectedRef}').");
        }

        var active = await db.Deployments.AnyAsync(d => d.AppId == appId &&
            (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Cloning ||
             d.Status == DeploymentStatus.Building || d.Status == DeploymentStatus.Starting), ct);
        if (active)
        {
            db.WebhookEvents.Add(NewEvent(appId, deliveryId, eventType, hash, WebhookEventStatus.Ignored, Ref: gitRef, CommitSha: after, Error: "deployment in progress; redeliver to retry"));
            await db.SaveChangesAsync(ct);
            return new WebhookResult(WebhookOutcome.Conflict, Message: "A deployment is already in progress. Redeliver to retry.");
        }

        var deployment = new Deployment
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Status = DeploymentStatus.Queued,
            ContainerPort = 8080,
            CommitSha = after.Length > 64 ? after[..64] : after,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        app.Status = AppStatus.Deploying;
        app.UpdatedAt = DateTimeOffset.UtcNow;

        db.Deployments.Add(deployment);
        db.DeploymentLogs.Add(new DeploymentLog
        {
            DeploymentId = deployment.Id,
            Line = $"Queued by webhook delivery {deliveryId} ({ShortSha(after)}).",
        });
        db.WebhookEvents.Add(new WebhookEvent
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            DeliveryId = deliveryId,
            EventType = eventType,
            Ref = gitRef,
            CommitSha = after,
            PayloadHash = hash,
            Status = WebhookEventStatus.Queued,
            DeploymentId = deployment.Id,
            ReceivedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a concurrent race on (AppId, DeliveryId): the winner's row is the truth.
            var winner = await db.WebhookEvents
                .FirstOrDefaultAsync(e => e.AppId == appId && e.DeliveryId == deliveryId, ct);
            if (winner is not null)
                return new WebhookResult(WebhookOutcome.Duplicate, winner.DeploymentId, "Delivery already processed.");
            throw;
        }

        return new WebhookResult(WebhookOutcome.Queued, deployment.Id);
    }

    private static WebhookEvent NewEvent(
        Guid appId, string deliveryId, string eventType, string hash,
        WebhookEventStatus status, string? Ref = null, string? CommitSha = null, string? Error = null) => new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            DeliveryId = deliveryId,
            EventType = eventType.Length > 30 ? eventType[..30] : eventType,
            Ref = Ref is { Length: > 200 } ? Ref[..200] : Ref,
            CommitSha = CommitSha is { Length: > 64 } ? CommitSha[..64] : CommitSha,
            PayloadHash = hash,
            Status = status,
            Error = Error is { Length: > 500 } ? Error[..500] : Error,
            ReceivedAt = DateTimeOffset.UtcNow,
        };

    private static string ShortSha(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
