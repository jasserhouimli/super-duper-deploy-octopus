namespace Octopus.Deployments.Webhooks;

public enum WebhookEventStatus
{
    Received = 0,
    Queued = 1,
    Ignored = 2,
    Duplicate = 3,
}

/// <summary>
/// Persisted receipt of an authenticated webhook delivery.
/// (TriggerId, ExternalEventId) uniqueness => (AppId, DeliveryId).
/// Unauthenticated payloads are never persisted (prevents DB-fill attacks).
/// </summary>
public sealed class WebhookEvent
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public string DeliveryId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? Ref { get; set; }
    public string? CommitSha { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public WebhookEventStatus Status { get; set; } = WebhookEventStatus.Received;
    public Guid? DeploymentId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Per-app webhook subscription. The secret must be stored (HMAC verification
/// needs the original value) — it lives in the control-plane DB and is only
/// ever returned at creation/rotation time, never logged.
/// </summary>
public sealed class AppWebhook
{
    public Guid AppId { get; set; }
    public string Secret { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
