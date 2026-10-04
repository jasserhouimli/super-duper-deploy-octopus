using Octopus.BuildingBlocks;

namespace Octopus.Apps;

/// <summary>
/// Per-app environment variable (secret reference). Values live in the
/// control-plane DB, are injected at `docker run` via --env-file, and are
/// never returned by list endpoints or written to logs.
/// </summary>
public sealed class AppEnvVar
{
    public Guid AppId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class AppEnvVars
{
    public const int MaxVarsPerApp = 50;
    public const int MaxKeyLength = 64;
    public const int MaxValueLength = 8192;
    public const int MaxTotalBytes = 64 * 1024;

    /// <summary>Validates a full replacement set. Returns sanitized copy on success.</summary>
    public static Result<Dictionary<string, string>> Validate(IDictionary<string, string>? vars)
    {
        if (vars is null)
            return Result<Dictionary<string, string>>.Fail("vars is required.");

        if (vars.Count > MaxVarsPerApp)
            return Result<Dictionary<string, string>>.Fail($"Too many variables (max {MaxVarsPerApp}).");

        var clean = new Dictionary<string, string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var (k, v) in vars)
        {
            var keyCheck = ValidateKey(k);
            if (!keyCheck.IsSuccess) return Result<Dictionary<string, string>>.Fail(keyCheck.Error);
            var valueCheck = ValidateValue(v);
            if (!valueCheck.IsSuccess) return Result<Dictionary<string, string>>.Fail($"{k}: {valueCheck.Error}");
            if (!clean.TryAdd(k, v))
                return Result<Dictionary<string, string>>.Fail($"Duplicate key: {k}.");
            total += k.Length + v.Length;
        }

        if (total > MaxTotalBytes)
            return Result<Dictionary<string, string>>.Fail("Total env size exceeds 64 KB.");

        return Result<Dictionary<string, string>>.Ok(clean);
    }

    public static Result<string> ValidateKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
            return Result<string>.Fail("Key is required (max 64 chars).");
        if (key.Length > 0 && (char.IsDigit(key[0]) || key[0] == ' '))
            return Result<string>.Fail($"Invalid key '{key}': must match ^[A-Za-z_][A-Za-z0-9_]*$.");
        foreach (var c in key)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return Result<string>.Fail($"Invalid key '{key}': must match ^[A-Za-z_][A-Za-z0-9_]*$.");
        }
        if (!char.IsAsciiLetter(key[0]) && key[0] != '_')
            return Result<string>.Fail($"Invalid key '{key}': must match ^[A-Za-z_][A-Za-z0-9_]*$.");
        return Result<string>.Ok(key);
    }

    public static Result<string> ValidateValue(string? value)
    {
        if (value is null)
            return Result<string>.Fail("Value must be a string (empty allowed).");
        if (value.Length > MaxValueLength)
            return Result<string>.Fail($"Value too long (max {MaxValueLength} chars).");
        if (value.Contains('\0') || value.Contains('\n') || value.Contains('\r'))
            return Result<string>.Fail("Value must not contain NUL or newline characters.");
        return Result<string>.Ok(value);
    }
}
