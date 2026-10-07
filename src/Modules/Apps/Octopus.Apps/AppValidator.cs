using Octopus.BuildingBlocks;
using Octopus.GitHub;

namespace Octopus.Apps;

public static class AppValidator
{
    public static Result<App> Create(string name, string repoUrl, string branch = "main")
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            return Result<App>.Fail("Name is required (max 80 chars).");

        var urlCheck = GitHubUrl.Validate(repoUrl);
        if (!urlCheck.IsSuccess)
            return Result<App>.Fail(urlCheck.Error);

        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 100 || branch.Contains(' ') || branch.Contains(".."))
            return Result<App>.Fail("Branch is invalid.");

        var slug = Slug.Normalize(name);
        if (!Slug.IsValid(slug))
            return Result<App>.Fail("Derived slug is invalid.");

        var app = new App
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Slug = slug,
            RepoUrl = urlCheck.Value!.CanonicalUrl,
            Branch = branch.Trim(),
            Status = AppStatus.Created,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return Result<App>.Ok(app);
    }

    /// <summary>
    /// Retargets an app at a new branch. Name/slug are immutable (URLs and
    /// container names derive from them). Future pushes and deployments use
    /// the new branch; already-queued work keeps its snapshot semantics.
    /// </summary>
    public static Result<App> ChangeBranch(App app, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 100 || branch.Contains(' ') || branch.Contains(".."))
            return Result<App>.Fail("Branch is invalid.");
        app.Branch = branch.Trim();
        app.UpdatedAt = DateTimeOffset.UtcNow;
        return Result<App>.Ok(app);
    }
}
