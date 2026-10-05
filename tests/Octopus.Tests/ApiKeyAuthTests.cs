using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;
using Octopus.Deployments.ApiKeys;

namespace Octopus.Tests;

public sealed class ApiKeyGateTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private OctopusDbContext _db = null!;
    private ApiKeyHasher.GeneratedKey _key = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _db = new OctopusDbContext(new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options);
        DbBootstrap.EnsureUpgraded(_db);
        _key = ApiKeyHasher.Generate("bot");
        _db.ApiKeys.Add(new ApiKey { Id = Guid.NewGuid(), Name = "bot", KeyPrefix = _key.KeyPrefix, KeyHash = _key.KeyHash });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private Task<ApiKeyGate.GateDecision> Check(string method, string path, string? bearer = null) =>
        ApiKeyGate.AuthorizeAsync(_db, method, path, bearer, CancellationToken.None);

    [Theory]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/apps/demo/")]
    [InlineData("POST", "/api/hooks/github/11111111-1111-1111-1111-111111111111")]
    public async Task Public_paths_need_no_key(string method, string path) =>
        Assert.True((await Check(method, path)).Allowed);

    [Fact]
    public async Task Valid_bearer_passes_and_records_use()
    {
        var d = await Check("GET", "/api/apps", _key.RawKey);
        Assert.True(d.Allowed);
        Assert.NotNull(d.KeyId);
        var row = await _db.ApiKeys.SingleAsync(k => k.KeyHash == _key.KeyHash);
        Assert.NotNull(row.LastUsedAt);
    }

    [Theory]
    [InlineData("GET", "/api/apps")]
    [InlineData("POST", "/api/apps")]
    public async Task Missing_or_malformed_bearer_fails(string method, string path)
    {
        Assert.False((await Check(method, path)).Allowed);
        Assert.False((await Check(method, path, "not-a-key")).Allowed);
    }

    [Fact]
    public async Task Revoked_key_fails()
    {
        var g = ApiKeyHasher.Generate("old");
        _db.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "old",
            KeyPrefix = g.KeyPrefix,
            KeyHash = g.KeyHash,
            RevokedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        Assert.False((await Check("GET", "/api/apps", g.RawKey)).Allowed);
    }

    [Fact]
    public async Task Setup_mode_allows_first_key_creation_only()
    {
        await _db.ApiKeys.ExecuteDeleteAsync();
        Assert.True((await Check("POST", "/api/keys")).Allowed);
        Assert.False((await Check("GET", "/api/apps")).Allowed);
    }
}
