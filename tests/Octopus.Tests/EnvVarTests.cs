using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Apps;
using Octopus.Deployments;
using Octopus.Runtime;

namespace Octopus.Tests;

public sealed class AppEnvVarValidatorTests
{
    [Fact]
    public void Accepts_valid_set()
    {
        var r = AppEnvVars.Validate(new Dictionary<string, string>
        {
            ["DATABASE_URL"] = "postgres://db:5432/app",
            ["API_KEY"] = "s3cret!",
            ["EMPTY_OK"] = "",
        });
        Assert.True(r.IsSuccess);
        Assert.Equal(3, r.Value!.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1ABC")]
    [InlineData("9LIVES")]
    [InlineData("HAS-DASH")]
    [InlineData("HAS SPACE")]
    [InlineData("lower.dot")]
    public void Rejects_bad_keys(string key)
    {
        Assert.False(AppEnvVars.ValidateKey(key).IsSuccess);
        Assert.False(AppEnvVars.Validate(new Dictionary<string, string> { [key] = "v" }).IsSuccess);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("_PRIVATE")]
    [InlineData("PORT")]
    [InlineData("mixed_Case123")]
    public void Accepts_good_keys(string key) =>
        Assert.True(AppEnvVars.ValidateKey(key).IsSuccess);

    [Fact]
    public void Rejects_newline_and_nul_values()
    {
        Assert.False(AppEnvVars.ValidateValue("a\nb").IsSuccess);
        Assert.False(AppEnvVars.ValidateValue("a\rb").IsSuccess);
        Assert.False(AppEnvVars.ValidateValue("a\0b").IsSuccess);
        Assert.False(AppEnvVars.Validate(new Dictionary<string, string> { ["K"] = "a\nb" }).IsSuccess);
    }

    [Fact]
    public void Rejects_oversized_sets()
    {
        var tooMany = Enumerable.Range(0, AppEnvVars.MaxVarsPerApp + 1)
            .ToDictionary(i => $"K{i}", i => "v");
        Assert.False(AppEnvVars.Validate(tooMany).IsSuccess);

        var tooBig = new Dictionary<string, string> { ["K"] = new string('x', AppEnvVars.MaxValueLength + 1) };
        Assert.False(AppEnvVars.Validate(tooBig).IsSuccess);

        Assert.False(AppEnvVars.Validate(null).IsSuccess);
    }
}

public sealed class DockerEnvFileTests
{
    [Fact]
    public void Env_file_is_sorted_key_equals_value()
    {
        var content = DockerRunner.BuildEnvFileContent(new Dictionary<string, string>
        {
            ["B_KEY"] = "2",
            ["A_KEY"] = "1",
        });
        Assert.Equal("A_KEY=1\nB_KEY=2\n", content);
    }

    [Fact]
    public void Start_args_use_env_file_not_values()
    {
        var args = DockerRunner.BuildStartArgs("octopus-demo", "img:1", 5100, 8080, "/tmp/x.env");
        Assert.Contains("--env-file \"/tmp/x.env\"", args);
        Assert.DoesNotContain("s3cret", args);
        Assert.Contains("-p 127.0.0.1:5100:8080", args);
    }

    [Fact]
    public void Start_args_omit_env_file_when_empty()
    {
        var args = DockerRunner.BuildStartArgs("c", "img", 5100, 8080, null);
        Assert.DoesNotContain("--env-file", args);
    }
}

public sealed class AppEnvVarPersistenceTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private OctopusDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _db = new OctopusDbContext(new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options);
        DbBootstrap.EnsureUpgraded(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Can_store_and_replace_env_set()
    {
        var appId = Guid.NewGuid();
        _db.Apps.Add(new App { Id = appId, Name = "demo", Slug = "demo", RepoUrl = "https://github.com/o/r.git", Branch = "main" });
        _db.AppEnvVars.Add(new AppEnvVar { AppId = appId, Key = "API_KEY", Value = "s3cret" });
        await _db.SaveChangesAsync();

        var keys = await _db.AppEnvVars.Where(e => e.AppId == appId).Select(e => e.Key).ToListAsync();
        Assert.Equal(["API_KEY"], keys);

        // Replacement: update + delete + insert.
        var row = await _db.AppEnvVars.FindAsync([appId, "API_KEY"]);
        row!.Value = "rotated";
        _db.AppEnvVars.Add(new AppEnvVar { AppId = appId, Key = "PORT", Value = "8080" });
        await _db.SaveChangesAsync();

        var all = await _db.AppEnvVars.Where(e => e.AppId == appId).OrderBy(e => e.Key).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("rotated", all.First(e => e.Key == "API_KEY").Value);
    }
}
