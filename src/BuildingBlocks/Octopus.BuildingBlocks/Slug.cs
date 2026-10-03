using System.Text.RegularExpressions;

namespace Octopus.BuildingBlocks;

/// <summary>
/// URL-safe app identifier used in routes and container names.
/// </summary>
public static partial class Slug
{
    private static readonly Regex ValidPattern = SlugPattern();

    public static string Normalize(string name)
    {
        var lower = (name ?? string.Empty).Trim().ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^a-z0-9]+", "-");
        lower = Regex.Replace(lower, @"-+", "-").Trim('-');
        if (lower.Length == 0) return "app";
        return lower.Length > 48 ? lower[..48].Trim('-') : lower;
    }

    public static bool IsValid(string slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug.Length <= 48 && ValidPattern.IsMatch(slug);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,46}[a-z0-9])?$")]
    private static partial Regex SlugPattern();
}
