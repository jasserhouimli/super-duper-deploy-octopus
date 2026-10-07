using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Octopus.Api;
using Octopus.Apps;
using Octopus.Deployments;
using Octopus.Deployments.Webhooks;
using Octopus.Routing;
using Octopus.Runtime;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<OctopusDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Octopus")
        ?? "Data Source=octopus.db"));

builder.Services.AddSingleton<OctopusProxyConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<OctopusProxyConfigProvider>());
builder.Services.AddReverseProxy();
builder.Services.AddHostedService<RouteRefresher>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    DbBootstrap.EnsureUpgraded(scope.ServiceProvider.GetRequiredService<OctopusDbContext>());
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.UseApiKeyAuth();

// ---- Apps ----
app.MapPost("/api/apps", async (CreateAppRequest req, OctopusDbContext db, CancellationToken ct) =>
{
    if (req is null) return Results.BadRequest(new { error = "Body required." });
    var check = AppValidator.Create(req.Name ?? "", req.RepoUrl ?? "", req.Branch ?? "main");
    if (!check.IsSuccess) return Results.BadRequest(new { error = check.Error });
    var quota = AppQuotas.Validate(req.MemoryMb, req.CpuMillicores);
    if (!quota.IsSuccess) return Results.BadRequest(new { error = quota.Error });

    var entity = check.Value!;
    entity.MaxMemoryMb = quota.Value.MemoryMb;
    entity.CpuMillicores = quota.Value.CpuMillicores;
    entity.Slug = await UniqueSlugAsync(db, entity.Slug, ct);
    entity.ContainerName = DockerRunner.ContainerName(entity.Slug);
    if (req.ContainerPort is > 0 and < 65536) { /* stored per-deployment for v0.1 */ }

    db.Apps.Add(entity);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/apps/{entity.Id}", ToDto(entity));
});

app.MapGet("/api/apps", async (OctopusDbContext db, CancellationToken ct) =>
{
    // NOTE: SQLite cannot ORDER BY DateTimeOffset server-side; sort in memory.
    var apps = await db.Apps.ToListAsync(ct);
    return Results.Ok(apps.OrderByDescending(a => a.CreatedAt).Select(ToDto).ToList());
});

app.MapGet("/api/apps/{id:guid}", async (Guid id, OctopusDbContext db, CancellationToken ct) =>
{
    var entity = await db.Apps.FindAsync([id], ct);
    return entity is null ? Results.NotFound() : Results.Ok(ToDto(entity));
});

app.MapDelete("/api/apps/{id:guid}", async (Guid id, OctopusDbContext db, ILogger<Program> log, CancellationToken ct) =>
{
    var entity = await db.Apps.FindAsync([id], ct);
    if (entity is null) return Results.NotFound();

    if (!string.IsNullOrEmpty(entity.ContainerName))
    {
        var runner = new DockerRunner();
        await runner.StopAndRemoveAsync(entity.ContainerName, _ => { }, ct);
        log.LogInformation("Removed container for app {Slug}", entity.Slug);
    }

    var deployments = await db.Deployments.Where(d => d.AppId == id).ToListAsync(ct);
    foreach (var d in deployments)
    {
        var logs = db.DeploymentLogs.Where(l => l.DeploymentId == d.Id);
        db.DeploymentLogs.RemoveRange(logs);
    }
    db.Deployments.RemoveRange(deployments);
    db.WebhookEvents.RemoveRange(db.WebhookEvents.Where(e => e.AppId == id));
    var sub = await db.AppWebhooks.FindAsync([id], ct);
    if (sub is not null) db.AppWebhooks.Remove(sub);
    db.Apps.Remove(entity);
    await db.SaveChangesAsync(ct);
    RefreshRoutes(app.Services, db);
    return Results.NoContent();
});

// ---- Deployments ----
app.MapPost("/api/apps/{id:guid}/deployments", async (Guid id, CreateDeploymentRequest? req, OctopusDbContext db, CancellationToken ct) =>
{
    var entity = await db.Apps.FindAsync([id], ct);
    if (entity is null) return Results.NotFound(new { error = "App not found." });

    var pending = await db.Deployments.AnyAsync(
        d => d.AppId == id && (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Cloning || d.Status == DeploymentStatus.Building || d.Status == DeploymentStatus.Starting), ct);
    if (pending) return Results.Conflict(new { error = "A deployment is already in progress for this app." });

    string? projectPath = null;
    if (!string.IsNullOrWhiteSpace(req?.ProjectPath))
    {
        var norm = Octopus.Runtime.BuildPlanDetector.NormalizeProjectPath(req.ProjectPath);
        if (!norm.IsSuccess) return Results.BadRequest(new { error = norm.Error });
        projectPath = norm.Value;
    }

    var deployment = new Deployment
    {
        Id = Guid.NewGuid(),
        AppId = id,
        Status = DeploymentStatus.Queued,
        ContainerPort = req?.ContainerPort is > 0 and < 65536 ? req.ContainerPort.Value : 8080,
        ProjectPath = projectPath,
        CreatedAt = DateTimeOffset.UtcNow,
    };
    entity.Status = AppStatus.Deploying;
    entity.UpdatedAt = DateTimeOffset.UtcNow;

    db.Deployments.Add(deployment);
    db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = deployment.Id, Line = $"Queued deployment {deployment.Id:N} for {entity.Slug}." });
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/deployments/{deployment.Id}", new { deployment.Id, status = deployment.Status.ToString() });
});

app.MapGet("/api/apps/{id:guid}/deployments", async (Guid id, string? status, OctopusDbContext db, CancellationToken ct) =>
{
    if (!await db.Apps.AnyAsync(a => a.Id == id, ct)) return Results.NotFound();
    DeploymentStatus? filter = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<DeploymentStatus>(status, ignoreCase: true, out var parsed))
            return Results.BadRequest(new { error = $"Unknown status '{status}'." });
        filter = parsed;
    }
    var list = await DeploymentQueries.ListByAppAsync(db, id, 50, filter, ct);
    return Results.Ok(list.Select(d => new
    {
        d.Id,
        d.AppId,
        status = d.Status.ToString(),
        d.CommitSha,
        d.Error,
        d.ContainerPort,
        d.ProjectPath,
        d.CreatedAt,
        d.StartedAt,
        d.FinishedAt,
    }));
});

app.MapGet("/api/deployments/{id:guid}", async (Guid id, OctopusDbContext db, CancellationToken ct) =>
{
    var d = await db.Deployments.FindAsync([id], ct);
    return d is null ? Results.NotFound() : Results.Ok(new
    {
        d.Id,
        d.AppId,
        status = d.Status.ToString(),
        d.CommitSha,
        d.Error,
        d.ContainerPort,
        d.ProjectPath,
        d.CreatedAt,
        d.StartedAt,
        d.FinishedAt,
    });
});

app.MapGet("/api/deployments/{id:guid}/logs", async (Guid id, int? take, OctopusDbContext db, CancellationToken ct) =>
{
    var n = Math.Clamp(take ?? 200, 1, 1000);
    var logs = await db.DeploymentLogs.Where(l => l.DeploymentId == id)
        .OrderByDescending(l => l.Id).Take(n).OrderBy(l => l.Id).ToListAsync(ct);
    return Results.Ok(logs.Select(l => new { l.At, l.Line }));
});

app.MapGet("/api/deployments/{id:guid}/logs/stream", async (
    Guid id, long? afterId, HttpContext ctx, OctopusDbContext db, CancellationToken ct) =>
{
    var deployment = await db.Deployments.FindAsync([id], ct);
    if (deployment is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new { error = "Deployment not found." }, ct);
        return;
    }

    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var cursor = Math.Max(0, afterId ?? 0);
    var deadline = DateTimeOffset.UtcNow.AddMinutes(5);

    static string Frame(object payload) =>
        "data: " + System.Text.Json.JsonSerializer.Serialize(payload) + "\n\n";

    try
    {
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            var batch = await DeploymentQueries.ListLogsAsync(db, id, cursor, 200, ct);
            foreach (var l in batch)
            {
                await ctx.Response.WriteAsync(Frame(new { l.Id, l.At, l.Line }), ct);
                cursor = l.Id;
            }
            await ctx.Response.Body.FlushAsync(ct);

            var fresh = await db.Deployments.FindAsync([id], ct);
            var terminal = fresh is null
                || fresh.Status is DeploymentStatus.Running or DeploymentStatus.Failed or DeploymentStatus.Stopped;
            if (terminal && batch.Count == 0)
                break;
            if (terminal)
                continue; // drain remaining lines without waiting
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
        }
        await ctx.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
    catch (OperationCanceledException) { /* client went away */ }
});

app.MapPost("/api/deployments/{id:guid}/cancel", async (Guid id, OctopusDbContext db, CancellationToken ct) =>
{
    var deployment = await db.Deployments.FindAsync([id], ct);
    if (deployment is null) return Results.NotFound(new { error = "Deployment not found." });

    var now = DateTimeOffset.UtcNow;
    var check = DeploymentActions.Cancel(deployment, now);
    if (!check.IsSuccess) return Results.Conflict(new { error = check.Error });

    db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = deployment.Id, Line = check.Value! });
    var app = await db.Apps.FindAsync([deployment.AppId], ct);
    if (app is not null && app.Status == AppStatus.Deploying)
    {
        // v1 has no previous-state memory: restore Running when a live deploy
        // exists, stay Deploying while siblings are pending, else idle.
        var siblings = await db.Deployments.Where(d => d.AppId == app.Id && d.Id != deployment.Id).ToListAsync(ct);
        app.Status = siblings.Any(d => d.Status == DeploymentStatus.Running)
            ? AppStatus.Running
            : siblings.Any(d => d.Status is DeploymentStatus.Queued or DeploymentStatus.Cloning or DeploymentStatus.Building or DeploymentStatus.Starting)
                ? AppStatus.Deploying
                : AppStatus.Created;
        app.UpdatedAt = now;
    }
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { deployment.Id, status = deployment.Status.ToString() });
});

app.MapPost("/api/deployments/{id:guid}/retry", async (Guid id, OctopusDbContext db, CancellationToken ct) =>
{
    var deployment = await db.Deployments.FindAsync([id], ct);
    if (deployment is null) return Results.NotFound(new { error = "Deployment not found." });

    var pending = await db.Deployments.AnyAsync(
        d => d.AppId == deployment.AppId && d.Id != deployment.Id
            && (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Cloning || d.Status == DeploymentStatus.Building || d.Status == DeploymentStatus.Starting), ct);
    if (pending) return Results.Conflict(new { error = "A deployment is already in progress for this app." });

    var check = DeploymentActions.Retry(deployment, DateTimeOffset.UtcNow);
    if (!check.IsSuccess) return Results.Conflict(new { error = check.Error });

    db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = deployment.Id, Line = check.Value! });
    var app = await db.Apps.FindAsync([deployment.AppId], ct);
    if (app is not null)
    {
        app.Status = AppStatus.Deploying;
        app.UpdatedAt = DateTimeOffset.UtcNow;
    }
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/deployments/{deployment.Id}", new { deployment.Id, status = deployment.Status.ToString() });
});

app.MapPost("/api/apps/{id:guid}/stop", async (Guid id, OctopusDbContext db, ILogger<Program> log, CancellationToken ct) =>
{
    var entity = await db.Apps.FindAsync([id], ct);
    if (entity is null) return Results.NotFound();
    var runner = new DockerRunner();
    await runner.StopAndRemoveAsync(entity.ContainerName ?? DockerRunner.ContainerName(entity.Slug), m => log.LogInformation("{Msg}", m), ct);
    entity.Status = AppStatus.Stopped;
    entity.UpdatedAt = DateTimeOffset.UtcNow;
    var running = (await db.Deployments.Where(d => d.AppId == id && d.Status == DeploymentStatus.Running)
        .ToListAsync(ct)).OrderByDescending(d => d.CreatedAt).FirstOrDefault();
    if (running is not null) { running.Status = DeploymentStatus.Stopped; running.FinishedAt = DateTimeOffset.UtcNow; }
    // Stop also cancels queued deployments so the worker cannot start a new
    // container for a stopped app. Already-claimed attempts run to completion
    // (v1 has no worker interruption) and are left untouched.
    var queued = await db.Deployments.Where(d => d.AppId == id && d.Status == DeploymentStatus.Queued).ToListAsync(ct);
    foreach (var q in queued)
    {
        if (DeploymentActions.Cancel(q, DateTimeOffset.UtcNow).IsSuccess)
            db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = q.Id, Line = "Cancelled: app stopped." });
    }
    await db.SaveChangesAsync(ct);
    RefreshRoutes(app.Services, db);
    return Results.Ok(ToDto(entity));
});

static object ToDto(App a) => new
{
    a.Id,
    a.Name,
    a.Slug,
    a.RepoUrl,
    a.Branch,
    status = a.Status.ToString(),
    a.TargetPort,
    a.MaxMemoryMb,
    a.CpuMillicores,
    url = $"/apps/{a.Slug}/",
    a.CreatedAt,
    a.UpdatedAt,
};

static async Task<string> UniqueSlugAsync(OctopusDbContext db, string baseSlug, CancellationToken ct)
{
    var slug = baseSlug;
    for (var i = 2; i < 100; i++)
    {
        if (!await db.Apps.AnyAsync(a => a.Slug == slug, ct)) return slug;
        slug = $"{baseSlug}-{i}";
    }
    return $"{baseSlug}-{Guid.NewGuid():N}"[..48];
}

static void RefreshRoutes(IServiceProvider services, OctopusDbContext db)
{
    try
    {
        var provider = services.GetRequiredService<OctopusProxyConfigProvider>();
        var running = db.Apps.Where(a => a.Status == AppStatus.Running && a.TargetPort > 0)
            .Select(a => new { a.Slug, a.TargetPort }).ToList()
            .Select(x => (x.Slug, x.TargetPort)).ToList();
        provider.Update(running);
    }
    catch { /* best effort */ }
}

// ---- API keys (control-plane auth; raw key shown once, hash-only storage) ----
app.MapPost("/api/keys", async (CreateKeyRequest? req, OctopusDbContext db, CancellationToken ct) =>
{
    Octopus.Deployments.ApiKeys.ApiKeyHasher.GeneratedKey gen;
    try
    {
        gen = Octopus.Deployments.ApiKeys.ApiKeyHasher.Generate(req?.Name ?? "");
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    var key = new Octopus.Deployments.ApiKeys.ApiKey
    {
        Id = Guid.NewGuid(),
        Name = (req?.Name ?? "").Trim(),
        KeyPrefix = gen.KeyPrefix,
        KeyHash = gen.KeyHash,
        CreatedAt = DateTimeOffset.UtcNow,
    };
    db.ApiKeys.Add(key);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/keys/{key.Id}", new
    {
        key.Id,
        key.Name,
        key.KeyPrefix,
        key = gen.RawKey, // shown once: never stored, never returned again
        key.CreatedAt,
    });
});

app.MapGet("/api/keys", async (OctopusDbContext db, CancellationToken ct) =>
{
    // NOTE: SQLite cannot ORDER BY DateTimeOffset server-side; sort in memory.
    var keys = await db.ApiKeys.ToListAsync(ct);
    return Results.Ok(keys.OrderByDescending(k => k.CreatedAt).Select(k => new
    {
        k.Id,
        k.Name,
        k.KeyPrefix,
        k.CreatedAt,
        k.RevokedAt,
        k.LastUsedAt,
    }).ToList());
});

app.MapPost("/api/keys/{id:guid}/revoke", async (Guid id, OctopusDbContext db, CancellationToken ct) =>
{
    var key = await db.ApiKeys.FindAsync([id], ct);
    if (key is null) return Results.NotFound(new { error = "Key not found." });
    key.RevokedAt ??= DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { key.Id, key.Name, key.KeyPrefix, key.CreatedAt, key.RevokedAt });
});

// ---- App quota (allowlisted memory/cpu caps) ----
app.MapPut("/api/apps/{id:guid}/quota", async (Guid id, SetQuotaRequest? req, OctopusDbContext db, CancellationToken ct) =>
{
    var entity = await db.Apps.FindAsync([id], ct);
    if (entity is null) return Results.NotFound(new { error = "App not found." });
    var check = AppQuotas.Validate(req?.MemoryMb, req?.CpuMillicores);
    if (!check.IsSuccess) return Results.BadRequest(new { error = check.Error });
    entity.MaxMemoryMb = check.Value.MemoryMb;
    entity.CpuMillicores = check.Value.CpuMillicores;
    entity.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(ToDto(entity));
});

// ---- App env vars (secret references; values never returned or logged) ----app.MapGet("/api/apps/{id:guid}/env", async (Guid id, OctopusDbContext db, CancellationToken ct) =>
{
    if (!await db.Apps.AnyAsync(a => a.Id == id, ct)) return Results.NotFound(new { error = "App not found." });
    var keys = await db.AppEnvVars.Where(e => e.AppId == id)
        .Select(e => e.Key).ToListAsync(ct);
    keys.Sort(StringComparer.Ordinal);
    return Results.Ok(new { keys, count = keys.Count });
});

app.MapPut("/api/apps/{id:guid}/env", async (Guid id, SetEnvRequest? req, OctopusDbContext db, CancellationToken ct) =>
{
    var entity = await db.Apps.FindAsync([id], ct);
    if (entity is null) return Results.NotFound(new { error = "App not found." });

    var check = AppEnvVars.Validate(req?.Vars);
    if (!check.IsSuccess) return Results.BadRequest(new { error = check.Error });
    var vars = check.Value!;

    var existing = await db.AppEnvVars.Where(e => e.AppId == id).ToListAsync(ct);
    var now = DateTimeOffset.UtcNow;
    foreach (var e in existing)
    {
        if (vars.TryGetValue(e.Key, out var next))
        {
            if (!string.Equals(e.Value, next, StringComparison.Ordinal))
            {
                e.Value = next;
                e.UpdatedAt = now;
            }
            vars.Remove(e.Key);
        }
        else
        {
            db.AppEnvVars.Remove(e);
        }
    }
    foreach (var (k, v) in vars)
        db.AppEnvVars.Add(new AppEnvVar { AppId = id, Key = k, Value = v, CreatedAt = now, UpdatedAt = now });

    entity.UpdatedAt = now;
    await db.SaveChangesAsync(ct);

    var keys = await db.AppEnvVars.Where(e => e.AppId == id)
        .Select(e => e.Key).ToListAsync(ct);
    keys.Sort(StringComparer.Ordinal);
    return Results.Ok(new { keys, count = keys.Count });
});

app.MapDelete("/api/apps/{id:guid}/env/{key}", async (Guid id, string key, OctopusDbContext db, CancellationToken ct) =>
{
    if (!await db.Apps.AnyAsync(a => a.Id == id, ct)) return Results.NotFound(new { error = "App not found." });
    if (!AppEnvVars.ValidateKey(key).IsSuccess) return Results.BadRequest(new { error = "Invalid key." });
    var entity = await db.AppEnvVars.FindAsync([id, key], ct);
    if (entity is null) return Results.NotFound(new { error = "Variable not found." });
    db.AppEnvVars.Remove(entity);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// ---- Webhooks (GitHub push -> queued deployment) ----
app.MapPost("/api/apps/{id:guid}/webhook-token", async (Guid id, OctopusDbContext db, CancellationToken ct) =>
{
    var entity = await db.Apps.FindAsync([id], ct);
    if (entity is null) return Results.NotFound(new { error = "App not found." });

    // 256-bit secret, base64url. Returned once here; verification needs the
    // original value, so it is stored in the control-plane DB (never logged).
    var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    var sub = await db.AppWebhooks.FindAsync([id], ct);
    if (sub is null)
    {
        sub = new AppWebhook { AppId = id, Secret = secret, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.AppWebhooks.Add(sub);
    }
    else
    {
        sub.Secret = secret;
        sub.UpdatedAt = DateTimeOffset.UtcNow;
    }
    await db.SaveChangesAsync(ct);
    return Results.Ok(new
    {
        secret,
        webhookUrl = $"/api/hooks/github/{id}",
        events = new[] { "push", "ping" },
        branch = entity.Branch,
        contentType = "application/json",
    });
});

app.MapGet("/api/apps/{id:guid}/webhook-events", async (Guid id, int? take, OctopusDbContext db, CancellationToken ct) =>
{
    if (!await db.Apps.AnyAsync(a => a.Id == id, ct)) return Results.NotFound();
    var n = Math.Clamp(take ?? 50, 1, 200);
    var events = await DeploymentQueries.ListWebhookEventsAsync(db, id, n, ct);
    return Results.Ok(events.Select(e => new
    {
        e.Id,
        e.DeliveryId,
        e.EventType,
        e.Ref,
        e.CommitSha,
        status = e.Status.ToString(),
        e.DeploymentId,
        e.Error,
        e.ReceivedAt,
    }));
});

app.MapPost("/api/hooks/github/{id:guid}", async (Guid id, HttpContext ctx, OctopusDbContext db, CancellationToken ct) =>
{
    var rawBody = await ReadBodyCappedAsync(ctx.Request, 1_000_000);
    if (rawBody is null)
        return Results.Json(new { error = "Payload too large (max 1 MB)." }, statusCode: 413);

    var headers = ctx.Request.Headers;
    var result = await WebhookService.HandleAsync(
        db, id,
        headers["X-GitHub-Event"].ToString(),
        headers["X-GitHub-Delivery"].ToString(),
        headers["X-Hub-Signature-256"].ToString(),
        rawBody, ct);

    return result.Outcome switch
    {
        WebhookOutcome.Queued => Results.Accepted($"/api/deployments/{result.DeploymentId}",
            new { delivery = headers["X-GitHub-Delivery"].ToString(), deploymentId = result.DeploymentId }),
        WebhookOutcome.Duplicate => Results.Ok(new { duplicate = true, deploymentId = result.DeploymentId, message = result.Message }),
        WebhookOutcome.Ping => Results.Ok(new { message = result.Message }),
        WebhookOutcome.Ignored => Results.Ok(new { ignored = true, message = result.Message }),
        WebhookOutcome.AppNotFound => Results.NotFound(new { error = result.Message }),
        WebhookOutcome.NotConfigured => Results.NotFound(new { error = result.Message }),
        WebhookOutcome.Unauthorized => Results.Json(new { error = result.Message }, statusCode: 401),
        WebhookOutcome.BadPayload => Results.BadRequest(new { error = result.Message }),
        WebhookOutcome.Conflict => Results.Conflict(new { error = result.Message }),
        _ => Results.BadRequest(new { error = result.Message }),
    };
});

app.MapReverseProxy();
app.Run();

/// <summary>Reads the request body as text with a hard cap. Null = over the cap.</summary>
static async Task<string?> ReadBodyCappedAsync(HttpRequest request, int maxChars)
{
    if (request.ContentLength > maxChars) return null;
    using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
    var sb = new StringBuilder();
    var buf = new char[8192];
    int read;
    while ((read = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
    {
        sb.Append(buf, 0, read);
        if (sb.Length > maxChars) return null;
    }
    return sb.ToString();
}

/// <summary>Polls Running apps and refreshes YARP routes. Best-effort; worker is source of truth for status.</summary>
public sealed class RouteRefresher(IServiceProvider services, ILogger<RouteRefresher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OctopusDbContext>();
                var provider = scope.ServiceProvider.GetRequiredService<OctopusProxyConfigProvider>();
                var running = await db.Apps.Where(a => a.Status == AppStatus.Running && a.TargetPort > 0)
                    .Select(a => new { a.Slug, a.TargetPort }).ToListAsync(stoppingToken);
                provider.Update(running.Select(x => (x.Slug, x.TargetPort)).ToList());
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogDebug(ex, "Route refresh failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

// Needed for WebApplicationFactory-style tests if added later.
public partial class Program;

public sealed record CreateAppRequest(string? Name, string? RepoUrl, string? Branch, int? ContainerPort, int? MemoryMb, int? CpuMillicores);
public sealed record CreateDeploymentRequest(int? ContainerPort, string? ProjectPath);
public sealed record SetQuotaRequest(int? MemoryMb, int? CpuMillicores);
public sealed record CreateKeyRequest(string? Name);
public sealed record SetEnvRequest(Dictionary<string, string>? Vars);
