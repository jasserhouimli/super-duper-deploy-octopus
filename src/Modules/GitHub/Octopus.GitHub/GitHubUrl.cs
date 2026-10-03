using Octopus.BuildingBlocks;

namespace Octopus.GitHub;

/// <summary>
/// Validates and normalizes public GitHub repo URLs.
/// v0.1: github.com https only, no credentials, no private repos, bounded length.
/// </summary>
public sealed record GitHubRepo(string Owner, string Repo, string CanonicalUrl)
{
    public string RedactedRef => $"{Owner}/{Repo}";
}

public static class GitHubUrl
{
    public static Result<GitHubRepo> Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500)
            return Result<GitHubRepo>.Fail("RepoUrl is required (max 500 chars).");

        url = url.Trim();

        if (url.Contains('@') && url.Contains(':'))
            return Result<GitHubRepo>.Fail("RepoUrl must not embed credentials.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Result<GitHubRepo>.Fail("RepoUrl must be an absolute https URL.");

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return Result<GitHubRepo>.Fail("RepoUrl must use https.");

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return Result<GitHubRepo>.Fail("v0.1 supports github.com repositories only.");

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
            return Result<GitHubRepo>.Fail("RepoUrl must look like https://github.com/owner/repo.");

        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        foreach (var part in new[] { owner, repo })
        {
            if (part.Length is 0 or > 100 || part.Contains(".."))
                return Result<GitHubRepo>.Fail("Repo owner/name is invalid.");
            if (!part.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
                return Result<GitHubRepo>.Fail("Repo owner/name contains invalid characters.");
        }

        return Result<GitHubRepo>.Ok(new GitHubRepo(owner, repo, $"https://github.com/{owner}/{repo}.git"));
    }
}
