namespace Octopus.Deployments.ApiKeys;

/// <summary>
/// Control-plane API key. Only the SHA-256 hash is stored — the raw key is
/// shown once at creation and never persisted or logged. Revocation is a
/// timestamp (null = active) so key IDs stay auditable after revocation.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>First 8 chars of the random part; lets operators identify keys without the secret.</summary>
    public string KeyPrefix { get; set; } = string.Empty;
    /// <summary>Lowercase hex SHA-256 of the raw key.</summary>
    public string KeyHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
