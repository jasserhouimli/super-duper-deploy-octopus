using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octopus.Apps;
using Octopus.Deployments;
using Octopus.Deployments.Webhooks;

namespace Octopus.Tests;

public sealed class SignatureTests
{
    private const string Secret = "test-secret-123";

    [Fact]
    public void Roundtrip_verifies()
    {
        var body = """{"ref":"refs/heads/main"}""";
        Assert.True(GitHubSignature.Verify(Secret, body, GitHubSignature.Sign(Secret, body)));
    }

    [Theory]
    [InlineData("""{"ref":"refs/heads/main"}""", """{"ref":"refs/heads/evil"}""")]
    public void Tampered_body_fails(string signed, string presented) =>
        Assert.False(GitHubSignature.Verify(Secret, presented, GitHubSignature.Sign(Secret, signed)));

    [Fact]
    public void Wrong_secret_fails() =>
        Assert.False(GitHubSignature.Verify("other", "{}", GitHubSignature.Sign(Secret, "{}")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=abc")]
    [InlineData("sha256=zzz")]
    [InlineData("sha256=abc")]
    public void Malformed_headers_fail(string? header) =>
        Assert.False(GitHubSignature.Verify(Secret, "{}", header));
}

public sealed class WebhookServiceTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private OctopusDbContext _db = null!;
    private Guid _appId;
    private const string Secret = "hook-secret";

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _db = new OctopusDbContext(new DbContextOptionsBuilder<OctopusDbContext>().UseSqlite(_conn).Options);
        await _db.Database.EnsureCreatedAsync();

        var app = AppValidator.Create("Hook App", "https://github.com/owner/repo", "main").Value!;
        _appId = app.Id;
        _db.Apps.Add(app);
        _db.AppWebhooks.Add(new AppWebhook { AppId = app.Id, Secret = Secret });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private static string PushBody(string gitRef = "refs/heads/main", string sha = "abc123def456abc123def456abc123def456abcd") =>
        "{\"ref\":\"" + gitRef + "\",\"after\":\"" + sha + "\",\"repository\":{\"full_name\":\"owner/repo\"}}";

    private Task<WebhookResult> SendAsync(string eventType, string delivery, string body, string? secret = Secret) =>
        WebhookService.HandleAsync(_db, _appId, eventType, delivery,
            secret is null ? null : GitHubSignature.Sign(secret, body), body, CancellationToken.None);

    [Fact]
    public async Task Push_queues_deployment()
    {
        var r = await SendAsync("push", "d-1", PushBody());
        Assert.Equal(WebhookOutcome.Queued, r.Outcome);
        Assert.NotNull(r.DeploymentId);
        Assert.Equal(1, await _db.Deployments.CountAsync());
    }

    [Fact]
    public async Task Redelivery_is_duplicate_without_new_deployment()
    {
        var body = PushBody();
        var first = await SendAsync("push", "d-2", body);
        var second = await SendAsync("push", "d-2", body);
        Assert.Equal(WebhookOutcome.Queued, first.Outcome);
        Assert.Equal(WebhookOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.DeploymentId, second.DeploymentId);
        Assert.Equal(1, await _db.Deployments.CountAsync(d => d.AppId == _appId));
    }

    [Fact]
    public async Task Bad_signature_is_unauthorized_and_not_persisted()
    {
        var r = await SendAsync("push", "d-3", PushBody(), secret: "wrong");
        Assert.Equal(WebhookOutcome.Unauthorized, r.Outcome);
        Assert.Equal(0, await _db.WebhookEvents.CountAsync(e => e.DeliveryId == "d-3"));
    }

    [Fact]
    public async Task Wrong_branch_is_ignored()
    {
        var r = await SendAsync("push", "d-4", PushBody("refs/heads/feature"));
        Assert.Equal(WebhookOutcome.Ignored, r.Outcome);
        Assert.Equal(0, await _db.Deployments.CountAsync());
    }

    [Fact]
    public async Task Second_push_while_active_conflicts()
    {
        await SendAsync("push", "d-5", PushBody());
        var r = await SendAsync("push", "d-6", PushBody());
        Assert.Equal(WebhookOutcome.Conflict, r.Outcome);
        Assert.Equal(1, await _db.Deployments.CountAsync(d => d.AppId == _appId));
    }

    [Fact]
    public async Task Ping_returns_pong()
    {
        var r = await SendAsync("ping", "d-7", """{"zen":"hi"}""");
        Assert.Equal(WebhookOutcome.Ping, r.Outcome);
    }

    [Fact]
    public async Task Receipts_are_pruned_to_newest_200()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 205; i++)
            _db.WebhookEvents.Add(new WebhookEvent
            {
                Id = Guid.NewGuid(),
                AppId = _appId,
                DeliveryId = $"old-{i}",
                EventType = "push",
                PayloadHash = "h",
                ReceivedAt = now.AddMinutes(-i),
            });
        await _db.SaveChangesAsync();

        var r = await SendAsync("ping", "d-8", """{"zen":"hi"}""");
        Assert.Equal(WebhookOutcome.Ping, r.Outcome);
        // 205 seeded + 1 new − 5 pruned (prune runs before the new receipt).
        Assert.Equal(201, await _db.WebhookEvents.CountAsync(e => e.AppId == _appId));
        Assert.Equal(1, await _db.WebhookEvents.CountAsync(e => e.DeliveryId == "d-8"));
    }
}
