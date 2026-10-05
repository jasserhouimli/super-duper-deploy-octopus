using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;
using Octopus.Deployments.ApiKeys;

namespace Octopus.Tests;

public sealed class ApiKeyHasherTests
{
    [Fact]
    public void Generated_key_has_expected_shape()
    {
        var g = ApiKeyHasher.Generate("ci");
        Assert.StartsWith("oct_", g.RawKey);
        Assert.Equal(8, g.KeyPrefix.Length);
        Assert.Equal(64, g.KeyHash.Length);
        Assert.True(ApiKeyHasher.IsWellFormed(g.RawKey));
        Assert.True(ApiKeyHasher.Verify(g.RawKey, g.KeyHash));
    }

    [Fact]
    public void Each_generation_is_unique() =>
        Assert.NotEqual(ApiKeyHasher.Generate("a").KeyHash, ApiKeyHasher.Generate("a").KeyHash);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bearer abc")]
    [InlineData("oct_short")]
    [InlineData("oct_has space")]
    [InlineData("xxx_oct_validlookingtoken1234567890")]
    public void Rejects_malformed_keys(string? key) =>
        Assert.False(ApiKeyHasher.IsWellFormed(key));

    [Fact]
    public void Wrong_key_does_not_verify()
    {
        var g = ApiKeyHasher.Generate("ci");
        Assert.False(ApiKeyHasher.Verify(g.RawKey + "x", g.KeyHash));
        Assert.False(ApiKeyHasher.Verify("oct_" + new string('a', 43), g.KeyHash));
        Assert.False(ApiKeyHasher.Verify(g.RawKey, new string('0', 64)));
    }

    [Fact]
    public void Blank_name_throws() =>
        Assert.Throws<ArgumentException>(() => ApiKeyHasher.Generate(""));
}

public sealed class ApiKeyPersistenceTests : IAsyncLifetime
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
    public async Task Can_store_and_lookup_by_hash()
    {
        var g = ApiKeyHasher.Generate("deploy-bot");
        _db.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "deploy-bot",
            KeyPrefix = g.KeyPrefix,
            KeyHash = g.KeyHash,
        });
        await _db.SaveChangesAsync();

        // Only the hash is stored — the raw key is nowhere in the row.
        var row = await _db.ApiKeys.SingleAsync(k => k.KeyHash == g.KeyHash);
        Assert.Equal("deploy-bot", row.Name);
        Assert.Null(row.RevokedAt);
        Assert.DoesNotContain(g.RawKey, row.KeyHash + row.KeyPrefix + row.Name);
        Assert.True(ApiKeyHasher.Verify(g.RawKey, row.KeyHash));
    }

    [Fact]
    public async Task Duplicate_hash_violates_unique_index()
    {
        var g = ApiKeyHasher.Generate("one");
        _db.ApiKeys.Add(new ApiKey { Id = Guid.NewGuid(), Name = "one", KeyPrefix = g.KeyPrefix, KeyHash = g.KeyHash });
        await _db.SaveChangesAsync();
        _db.ApiKeys.Add(new ApiKey { Id = Guid.NewGuid(), Name = "two", KeyPrefix = g.KeyPrefix, KeyHash = g.KeyHash });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }
}
