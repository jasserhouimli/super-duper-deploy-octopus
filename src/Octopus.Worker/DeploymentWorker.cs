using Microsoft.EntityFrameworkCore;
using Octopus.Apps;
using Octopus.Deployments;
using Octopus.GitHub;
using Octopus.Runtime;

namespace Octopus.Worker;

/// <summary>
/// Claims Queued deployments (oldest first) and runs Clone -> Build -> Start.
/// Single-worker claim is sufficient for v0.1; document the limit and add leases for multi-worker.
/// Stale Running recovery: on startup, requeue Cloning/Building/Starting as Queued (crash recovery).
/// Build: repo Dockerfile wins, else the dotnet buildpack generates one (see Runtime).
/// </summary>
public sealed class DeploymentWorker(
    IServiceProvider services,
    IConfiguration config,
    ILogger<DeploymentWorker> log) : BackgroundService
{
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
        var stale = await db.Deployments.Where(d =>
            d.Status == DeploymentStatus.Cloning || d.Status == DeploymentStatus.Building || d.Status == DeploymentStatus.Starting).ToListAsync(ct);
        foreach (var d in stale)
        {
            d.Status = DeploymentStatus.Queued;
            d.Error = "Requeued after worker restart.";
            db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = d.Id, Line = "Worker restarted: requeued stale deployment." });
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

        // v0.1 single-claimer: mark Cloning immediately. Multi-worker needs a lease column + atomic UPDATE.
        next.Status = DeploymentStatus.Cloning;
        next.StartedAt = DateTimeOffset.UtcNow;
        db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = next.Id, Line = "Claimed by worker." });
        await db.SaveChangesAsync(ct);
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
            await db.SaveChangesAsync(ct);

            var urlCheck = GitHubUrl.Validate(app.RepoUrl);
            if (!urlCheck.IsSuccess) throw new InvalidOperationException(urlCheck.Error);
            var repo = urlCheck.Value!;

            var clone = await GitCloner.CloneAsync(repo.CanonicalUrl, repo.Owner, repo.Repo, app.Branch, workDir, ct);
            await db.SaveChangesAsync(ct); // flush clone log lines saved via Log? (clone logs go to result)
            if (!clone.IsSuccess) throw new InvalidOperationException(clone.Error);
            deployment.CommitSha = clone.Value;
            Log($"Cloned {repo.RedactedRef}@{deployment.CommitSha}.");

            deployment.Status = DeploymentStatus.Building;
            await db.SaveChangesAsync(ct);

            var image = DockerRunner.ImageName(app.Slug, deployment.Id);
            // Persist partial state so polling sees progress.
            await db.SaveChangesAsync(ct);
            var planCheck = BuildPlanDetector.Detect(workDir, deployment.ProjectPath);
            if (!planCheck.IsSuccess || planCheck.Value is null)
                throw new InvalidOperationException($"Buildpack: {planCheck.Error}");
            var plan = planCheck.Value;
            Log(plan is DotnetPlan dotnet
                ? $"Build plan: dotnet buildpack ({dotnet.ProjectRelativePath})."
                : "Build plan: repo Dockerfile.");
            var build = await docker.BuildWithPlanAsync(workDir, image, plan, Log, ct);
            await db.SaveChangesAsync(ct);
            if (!build.IsSuccess) throw new InvalidOperationException(build.Error);

            deployment.Status = DeploymentStatus.Starting;
            await db.SaveChangesAsync(ct);

            var taken = await db.Apps.Where(a => a.Status == AppStatus.Running && a.Id != app.Id)
                .Select(a => a.TargetPort).ToListAsync(ct);
            var port = app.TargetPort is >= PortAllocator.MinPort and <= PortAllocator.MaxPort
                && !taken.Contains(app.TargetPort)
                ? app.TargetPort
                : PortAllocator.FindFreePort(new HashSet<int>(taken));

            var start = await docker.StartWithEnvAndQuotaAsync(
                DockerRunner.ContainerName(app.Slug), image, port, deployment.ContainerPort,
                await LoadEnvAsync(db, app.Id, ct),
                DockerRunner.FormatMemory(app.MaxMemoryMb), DockerRunner.FormatCpus(app.CpuMillicores),
                Log, ct);
            await db.SaveChangesAsync(ct);
            if (!start.IsSuccess) throw new InvalidOperationException(start.Error);

            // Health gate: the container must accept TCP on its host port and (when
            // docker reports state) look healthy. A bad image fails the deployment
            // instead of being marked Running.
            Log($"Probing readiness on 127.0.0.1:{port} (30s budget).");
            var ready = await ContainerHealth.WaitForTcpAsync(
                port, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), ct);
            var inspect = await DockerInspect.InspectAsync(DockerRunner.ContainerName(app.Slug), ct);
            var inspectHealthy = !inspect.IsSuccess || inspect.Value is null
                ? (bool?)null // docker gave no usable state: TCP probe decides
                : DockerInspect.IsHealthy(inspect.Value);
            if (!ready || inspectHealthy == false)
            {
                Log(ready
                    ? "Readiness failed: container state is not healthy; stopping."
                    : "Readiness failed: port did not accept connections within 30s; stopping.");
                await docker.StopAndRemoveAsync(DockerRunner.ContainerName(app.Slug), _ => { }, ct);
                throw new InvalidOperationException("Container did not become ready within 30s.");
            }
            Log("Readiness passed.");

            deployment.Status = DeploymentStatus.Running;
            deployment.FinishedAt = DateTimeOffset.UtcNow;
            app.Status = AppStatus.Running;
            app.TargetPort = port;
            app.ContainerName = DockerRunner.ContainerName(app.Slug);
            app.UpdatedAt = DateTimeOffset.UtcNow;
            Log($"Running at /apps/{app.Slug}/ -> 127.0.0.1:{port} (container:{deployment.ContainerPort}).");
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            deployment.Status = DeploymentStatus.Failed;
            deployment.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            deployment.FinishedAt = DateTimeOffset.UtcNow;
            app.Status = AppStatus.Failed;
            app.UpdatedAt = DateTimeOffset.UtcNow;
            db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = deployment.Id, Line = $"FAILED: {deployment.Error}" });
            await db.SaveChangesAsync(ct);
            log.LogWarning(ex, "Deployment {Id} failed.", deployment.Id);
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
            catch (Exception ex) { log.LogDebug(ex, "Workspace cleanup failed."); }
        }
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
