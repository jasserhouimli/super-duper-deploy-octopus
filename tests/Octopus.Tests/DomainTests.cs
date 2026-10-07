using Octopus.Apps;
using Octopus.BuildingBlocks;
using Octopus.GitHub;

namespace Octopus.Tests;

public sealed class SlugTests
{
    [Theory]
    [InlineData("My App!", "my-app")]
    [InlineData("  Hello   World  ", "hello-world")]
    [InlineData("a", "a")]
    public void Normalizes_names(string input, string expected) =>
        Assert.Equal(expected, Slug.Normalize(input));

    [Theory]
    [InlineData("my-app", true)]
    [InlineData("a", true)]
    [InlineData("My-App", false)]
    [InlineData("-lead", false)]
    [InlineData("", false)]
    public void Validates_slugs(string slug, bool expected) =>
        Assert.Equal(expected, Slug.IsValid(slug));
}

public sealed class GitHubUrlTests
{
    [Fact]
    public void Accepts_owner_repo()
    {
        var r = GitHubUrl.Validate("https://github.com/owner/repo");
        Assert.True(r.IsSuccess);
        Assert.Equal("https://github.com/owner/repo.git", r.Value!.CanonicalUrl);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://user:pass@github.com/owner/repo")]
    [InlineData("https://github.com/owner")]
    [InlineData("not-a-url")]
    public void Rejects_bad_urls(string url) =>
        Assert.False(GitHubUrl.Validate(url).IsSuccess);
}

public sealed class AppValidatorTests
{
    [Fact]
    public void Creates_app_with_canonical_url()
    {
        var r = AppValidator.Create("Demo", "https://github.com/owner/repo", "main");
        Assert.True(r.IsSuccess);
        Assert.Equal("demo", r.Value!.Slug);
        Assert.Equal("https://github.com/owner/repo.git", r.Value.RepoUrl);
    }

    [Fact]
    public void Rejects_empty_name() =>
        Assert.False(AppValidator.Create("", "https://github.com/o/r").IsSuccess);

    [Fact]
    public void Rejects_path_traversal_branch() =>
        Assert.False(AppValidator.Create("x", "https://github.com/o/r", "../evil").IsSuccess);

    [Fact]
    public void ChangeBranch_updates_and_timestamps()
    {
        var app = AppValidator.Create("Demo", "https://github.com/owner/repo", "main").Value!;
        var before = app.UpdatedAt;
        var r = AppValidator.ChangeBranch(app, "release");
        Assert.True(r.IsSuccess);
        Assert.Equal("release", app.Branch);
        Assert.True(app.UpdatedAt >= before);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../evil")]
    [InlineData("has space")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ChangeBranch_rejects_bad_branches(string branch)
    {
        var app = AppValidator.Create("Demo", "https://github.com/owner/repo", "main").Value!;
        Assert.False(AppValidator.ChangeBranch(app, branch).IsSuccess);
        Assert.Equal("main", app.Branch);
    }
}
