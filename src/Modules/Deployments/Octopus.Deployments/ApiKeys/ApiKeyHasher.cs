using System.Security.Cryptography;
using System.Text;

namespace Octopus.Deployments.ApiKeys;

/// <summary>
/// API key issuance and verification. Keys look like `oct_&lt;base64url(32 bytes)&gt;`.
/// Storage holds only the SHA-256 hash; comparison is constant-time.
/// </summary>
public static class ApiKeyHasher
{
    public const string SchemePrefix = "oct_";
    public const int PrefixLength = 8;

    public sealed record GeneratedKey(string RawKey, string KeyPrefix, string KeyHash);

    public static GeneratedKey Generate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new ArgumentException("Name is required (max 80 chars).", nameof(name));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var raw = SchemePrefix + token;
        return new GeneratedKey(raw, token[..PrefixLength], Hash(raw));
    }

    public static string Hash(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    public static bool Verify(string rawKey, string expectedHash)
    {
        if (string.IsNullOrEmpty(rawKey) || string.IsNullOrEmpty(expectedHash))
            return false;
        byte[] actual, expected;
        try
        {
            actual = Convert.FromHexString(Hash(rawKey));
            expected = Convert.FromHexString(expectedHash);
        }
        catch
        {
            return false;
        }
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Shape check before any DB lookup (format only, not authenticity).</summary>
    public static bool IsWellFormed(string? rawKey)
    {
        if (string.IsNullOrEmpty(rawKey) || !rawKey.StartsWith(SchemePrefix, StringComparison.Ordinal))
            return false;
        var token = rawKey[SchemePrefix.Length..];
        if (token.Length is < 20 or > 100) return false;
        foreach (var c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return false;
        }
        return true;
    }
}
