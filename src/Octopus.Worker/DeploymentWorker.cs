using Microsoft.EntityFrameworkCore;
using Octopus.Apps;
using Octopus.Deployments;
using Octopus.GitHub;
using Octopus.Runtime;

namespace Octopus.Worker;

/// <summary>
/// Claims Queued deployments (oldest first) and runs Clone -> Build -> Start.
/// Claims carry a 2-minute lease renewed by heartbeat; a restarting worker only
/// requeues deployments whose lease already lapsed, so live work is not stolen.
/// Stale-lease recovery still cannot interrupt an already-claimed attempt.
/// Build: repo Dockerfile wins, else the dotnet buildpack generates one (see Runtime).
/// </summary>
public sealed class DeploymentWorker(
    IServiceProvider services,
    IConfiguration config,
    ILogger<DeploymentWorker> log) : BackgroundService
{
    /// <summary>How long a claim lasts without a heartbeat (see heartbeat loop in RunDeploymentAsync).</summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    /// <summary>Renewal cadence, well within <see cref="LeaseDuration"/>.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    internal static string WorkerId { get; } =
        ResolveWorkerId();

    private static string ResolveWorkerId()
    {
        var configured = Environment.GetEnvironmentVariable("Octopus__WorkerId");
        if (!string.IsNullOrWhiteSpace(configured) && configured.Length <= 100)
            return configured.Trim();
        return $"{Environment.MachineName}:{Environment.ProcessId}";
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Handoff: release our active leases so recovery is immediate instead
        // of waiting for them to lapse. In-flight docker work is left alone.
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OctopusDbContext>();
            var released = await DeploymentLeases.ReleaseOwnedAsync(db, WorkerId, cancellationToken);
            if (released > 0)
                log.LogInformation("Released {Count} lease(s) on shutdown.", released);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Lease release on shutdown failed.");
        }
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverStaleAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await ClaimNextAsync(stoppingToken);
                if (claimed is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }
                await RunDeploymentAsync(claimed.Value.deploymentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Deployment loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RecoverStaleAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopusDbContext>();
        // Only lapsed leases are recoverable: a live lease means another worker
        // is actively working the deployment, so it must not be stolen.
        var stale = await DeploymentLeases.ListRecoverableAsync(db, DateTimeOffset.UtcNow, ct);
        foreach (var d in stale)
        {
            d.Status = DeploymentStatus.Queued;
            d.LeaseOwner = null;
            d.LeaseExpiresAt = null;
            d.Error = "Requeued: lease lapsed without heartbeat.";
            db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = d.Id, Line = "Lease lapsed: requeued stale deployment." });
        }
        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("Requeued {Count} stale deployments.", stale.Count);
        }
    }

    private async Task<(Guid deploymentId, Guid appId)?> ClaimNextAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopusDbContext>();
        // SQLite cannot ORDER BY DateTimeOffset server-side; queued rows are few, sort in memory.
        var next = (await db.Deployments.Where(d => d.Status == DeploymentStatus.Queued)
            .ToListAsync(ct)).OrderBy(d => d.CreatedAt).FirstOrDefault();
        if (next is null) return null;

        // Lease claim: LeaseExpiresAt is a concurrency token, so two workers
        // racing the same row resolve to exactly one winner (the loser gets
        // DbUpdateConcurrencyException and retries on the next poll).
        next.Status = DeploymentStatus.Cloning;
        next.StartedAt = DateTimeOffset.UtcNow;
        next.LeaseOwner = WorkerId;
        next.LeaseExpiresAt = DateTimeOffset.UtcNow.Add(LeaseDuration);
        db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = next.Id, Line = $"Claimed by worker {WorkerId}." });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            log.LogDebug("Lost claim race for deployment {Id}.", next.Id);
            return null;
        }
        return (next.Id, next.AppId);
    }

    private async Task RunDeploymentAsync(Guid deploymentId, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopusDbContext>();
        var docker = new DockerRunner();

        var deployment = await db.Deployments.FindAsync([deploymentId], ct);
        if (deployment is null) return;
        var app = await db.Apps.FindAsync([deployment.AppId], ct);
        if (app is null)
        {
            deployment.Status = DeploymentStatus.Failed;
            deployment.Error = "App deleted.";
            await db.SaveChangesAsync(ct);
            return;
        }

        var workspace = config["Octopus:Workspace"]
            ?? Path.Combine(Environment.CurrentDirectory, "workspace");
        var workDir = Path.Combine(workspace, app.Slug, deployment.Id.ToString("N"));

        // The heartbeat renews the lease on a separate context while the run
        // below holds this one. Both sides serialize through leaseLock, and
        // every save refreshes the concurrency-token original — otherwise the
        // heartbeat's renewals would trip optimistic-concurrency failures on
        // the run's own saves.
        using var leaseLock = new SemaphoreSlim(1, 1);
        async Task SaveAsync()
        {
            await leaseLock.WaitAsync(ct);
            try
            {
                var current = await db.Deployments.Where(x => x.Id == deployment.Id)
                    .Select(x => x.LeaseExpiresAt).FirstOrDefaultAsync(ct);
                db.Entry(deployment).Property(d => d.LeaseExpiresAt).OriginalValue = current;
                await SaveAsync();
            }
            finally
            {
                leaseLock.Release();
            }
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = RenewLeaseLoopAsync(deployment.Id, leaseLock, heartbeatCts.Token);

        try
        {
            void Log(string line)
            {
                var safe = line.Length > 1000 ? line[..1000] : line;
                db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = deployment.Id, Line = safe });
                log.LogInformation("[{Slug}:{Dep}] {Line}", app.Slug, deployment.Id.ToString("N")[..8], safe);
            }

            Log($"Deploying {app.Slug} from {Redact(app.RepoUrl)} branch {app.Branch}.");
            Directory.CreateDirectory(workspace);

            deployment.Status = DeploymentStatus.Cloning;
            await SaveAsync();

            var urlCheck = GitHubUrl.Validate(app.RepoUrl);
            if (!urlCheck.IsSuccess) throw new InvalidOperationException(urlCheck.Error);
            var repo = urlCheck.Value!;

            var clone = await GitCloner.CloneAsync(repo.CanonicalUrl, repo.Owner, repo.Repo, app.Branch, workDir, ct);
            await SaveAsync(); // flush clone log lines saved via Log? (clone logs go to result)
            if (!clone.IsSuccess) throw new InvalidOperationException(clone.Error);
            deployment.CommitSha = clone.Value;
            Log($"Cloned {repo.RedactedRef}@{deployment.CommitSha}.");

            deployment.Status = DeploymentStatus.Building;
            await SaveAsync();

            var image = DockerRunner.ImageName(app.Slug, deployment.Id);
            // Persist partial state so polling sees progress.
            await SaveAsync();
            var planCheck = BuildPlanDetector.Detect(workDir, deployment.ProjectPath, deployment.DockerfilePath);
            if (!planCheck.IsSuccess || planCheck.Value is null)
                throw new InvalidOperationException($"Buildpack: {planCheck.Error}");
            var plan = planCheck.Value;
            Log(plan switch
            {
                DotnetPlan dotnet => $"Build plan: dotnet buildpack ({dotnet.ProjectRelativePath}).",
                DockerfilePlan { DockerfileRelativePath: not null } explicit_ => $"Build plan: repo Dockerfile ({explicit_.DockerfileRelativePath}).",
                _ => "Build plan: repo Dockerfile.",
            });
            var build = await docker.BuildWithPlanAsync(workDir, image, plan, Log, ct);
            await SaveAsync();
            if (!build.IsSuccess) throw new InvalidOperationException(build.Error);

            deployment.Status = DeploymentStatus.Starting;
            await SaveAsync();

            // Blue/green: the sidecar starts beside the live container on a fresh
            // port (the old port stays bound until promote), so a bad image
            // never takes the app down.
            var taken = await db.Apps.Where(a => a.Status == AppStatus.Running && a.Id != app.Id)
                .Select(a => a.TargetPort).ToListAsync(ct);
            var busy = new HashSet<int>(taken);
            if (app.TargetPort > 0) busy.Add(app.TargetPort);
            var port = PortAllocator.FindFreePort(busy);

            var canonical = DockerRunner.ContainerName(app.Slug);
            var sidecar = DockerRunner.SidecarName(app.Slug, deployment.Id);
            Log($"Starting sidecar {sidecar} for readiness on 127.0.0.1:{port}.");
            var start = await docker.StartWithEnvAndQuotaAsync(
                sidecar, image, port, deployment.ContainerPort,
                await LoadEnvAsync(db, app.Id, ct),
                DockerRunner.FormatMemory(app.MaxMemoryMb), DockerRunner.FormatCpus(app.CpuMillicores),
                Log, ct, stopExisting: false);
            await SaveAsync();
            if (!start.IsSuccess) throw new InvalidOperationException(start.Error);

            // Health gate: the sidecar must accept TCP on its host port and (when
            // docker reports state) look healthy. Unhealthy sidecars are removed;
            // the live container keeps serving.
            Log($"Probing readiness on 127.0.0.1:{port} (30s budget).");
            var ready = await ContainerHealth.WaitForTcpAsync(
                port, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), ct);
            var inspect = await DockerInspect.InspectAsync(sidecar, ct);
            var inspectHealthy = !inspect.IsSuccess || inspect.Value is null
                ? (bool?)null // docker gave no usable state: TCP probe decides
                : DockerInspect.IsHealthy(inspect.Value);
            if (!ready || inspectHealthy == false)
            {
                Log(ready
                    ? "Readiness failed: container state is not healthy; removing sidecar."
                    : "Readiness failed: port did not accept connections within 30s; removing sidecar.");
                Log("--- sidecar log tail (last 50 lines) ---");
                await docker.LogsAsync(sidecar, 50, Log, ct);
                await docker.StopAndRemoveAsync(sidecar, _ => { }, ct);
                throw new InvalidOperationException("Container did not become ready within 30s.");
            }
            Log("Readiness passed.");

            Log($"Promoting sidecar to {canonical}.");
            var promote = await docker.PromoteAsync(canonical, sidecar, Log, ct);
            await SaveAsync();
            if (!promote.IsSuccess) throw new InvalidOperationException(promote.Error);

            // The app may have been deleted mid-run: never report Running or
            // leave a container behind for an app that no longer exists.
            // (Fresh read — the run's tracked entity cannot see the delete.)
            var appExists = await DeploymentQueries.AppExistsAsync(db, app.Id, ct);
            if (!appExists)
            {
                Log("App was deleted during deploy; removing container.");
                await docker.StopAndRemoveAsync(canonical, _ => { }, ct);
                try
                {
                    deployment.Status = DeploymentStatus.Failed;
                    deployment.Error = "App deleted during deploy.";
                    deployment.FinishedAt = DateTimeOffset.UtcNow;
                    await SaveAsync();
                }
                catch { /* record went with the app */ }
                return;
            }

            deployment.Status = DeploymentStatus.Running;
            deployment.FinishedAt = DateTimeOffset.UtcNow;
            app.Status = AppStatus.Running;
            app.TargetPort = port;
            app.ContainerName = canonical;
            app.UpdatedAt = DateTimeOffset.UtcNow;
            Log($"Running at /apps/{app.Slug}/ -> 127.0.0.1:{port} (container:{deployment.ContainerPort}).");
            await SaveAsync();
            await DeploymentQueries.PruneAsync(db, app.Id, ct: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            deployment.Status = DeploymentStatus.Failed;
            deployment.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            deployment.FinishedAt = DateTimeOffset.UtcNow;
            app.Status = AppStatus.Failed;
            app.UpdatedAt = DateTimeOffset.UtcNow;
            db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = deployment.Id, Line = $"FAILED: {deployment.Error}" });
            await SaveAsync();
            await DeploymentQueries.PruneAsync(db, app.Id, ct: ct);
            log.LogWarning(ex, "Deployment {Id} failed.", deployment.Id);
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch { /* heartbeat teardown is best effort */ }
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
            catch (Exception ex) { log.LogDebug(ex, "Workspace cleanup failed."); }
        }
    }

    /// <summary>
    /// Renews this worker's lease until the run ends or the lease is lost.
    /// Renewal is a guarded UPDATE (owner must still match), serialized with
    /// the run's saves through <paramref name="leaseLock"/>.
    /// </summary>
    private async Task RenewLeaseLoopAsync(Guid deploymentId, SemaphoreSlim leaseLock, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                var held = false;
                try
                {
                    await leaseLock.WaitAsync(ct);
                    held = true;
                    using var scope = services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<OctopusDbContext>();
                    var renewed = await db.Deployments
                        .Where(d => d.Id == deploymentId && d.LeaseOwner == WorkerId)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(d => d.LeaseExpiresAt, DateTimeOffset.UtcNow.Add(LeaseDuration)), ct);
                    if (renewed == 0)
                    {
                        log.LogWarning("Lease for deployment {Id} lost; stopping heartbeat.", deploymentId);
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { log.LogDebug(ex, "Lease renewal failed."); }
                finally { if (held) leaseLock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* run ended */ }
    }

    private static async Task<Dictionary<string, string>> LoadEnvAsync(OctopusDbContext db, Guid appId, CancellationToken ct)
    {
        // Values go straight to the docker --env-file; only the count is logged by the caller.
        var rows = await db.AppEnvVars.Where(e => e.AppId == appId).ToListAsync(ct);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
            dict[r.Key] = r.Value;
        return dict;
    }

    private static string Redact(string url)
    {
        // URLs are validated credential-free, but never echo full URLs anyway.
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath.Trim('/').Split('/') is { Length: >= 2 } parts)
            return $"{u.Host}/{parts[0]}/{parts[1].Replace(".git", "")}";
        return "github.com/<owner>/<repo>";
    }
}
