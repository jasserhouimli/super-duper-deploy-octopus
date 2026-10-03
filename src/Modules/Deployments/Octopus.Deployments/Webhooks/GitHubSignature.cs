using System.Security.Cryptography;
using System.Text;

namespace Octopus.Deployments.Webhooks;

/// <summary>
/// GitHub webhook HMAC-SHA256 verification (X-Hub-Signature-256).
/// Constant-time comparison; rejects malformed headers without leaking timing.
/// </summary>
public static class GitHubSignature
{
    private const string Prefix = "sha256=";

    public static bool Verify(string secret, string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader))
            return false;
        if (!signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(signatureHeader[Prefix.Length..]);
        }
        catch
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var actual = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string Sign(string secret, string rawBody)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Prefix + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
    }

    public static string PayloadHash(string rawBody) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
}
