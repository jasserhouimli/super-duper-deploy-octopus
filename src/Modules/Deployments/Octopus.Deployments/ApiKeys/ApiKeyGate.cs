using Microsoft.EntityFrameworkCore;

namespace Octopus.Deployments.ApiKeys;

/// <summary>
/// Framework-free control-plane auth decision: public paths, bearer lookup,
/// and first-key setup mode. The HTTP middleware in Octopus.Api is a thin
/// adapter over this so the rules stay unit-testable without a web host.
/// </summary>
public static class ApiKeyGate
{
    public sealed record GateDecision(bool Allowed, Guid? KeyId);

    public static bool IsPublicPath(string method, string path)
    {
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            return true; // deployed apps (YARP), openapi in dev
        if (path.StartsWith("/api/hooks/github", StringComparison.OrdinalIgnoreCase))
            return true; // HMAC-authenticated webhook receiver
        return false;
    }

    public static async Task<GateDecision> AuthorizeAsync(
        OctopusDbContext db, string method, string path, string? bearerToken, CancellationToken ct)
    {
        if (IsPublicPath(method, path))
            return new GateDecision(true, null);

        if (!string.IsNullOrEmpty(bearerToken) && ApiKeyHasher.IsWellFormed(bearerToken))
        {
            var hash = ApiKeyHasher.Hash(bearerToken);
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
            if (key is not null && key.RevokedAt is null)
            {
                key.LastUsedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return new GateDecision(true, key.Id);
            }
            return new GateDecision(false, null);
        }

        // Setup mode: creating the very first key needs no key.
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/keys", StringComparison.OrdinalIgnoreCase)
            && !await db.ApiKeys.AnyAsync(k => k.RevokedAt == null, ct))
            return new GateDecision(true, null);

        return new GateDecision(false, null);
    }
}
