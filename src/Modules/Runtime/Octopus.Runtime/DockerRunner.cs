using Octopus.BuildingBlocks;

namespace Octopus.Runtime;

/// <summary>
/// Docker runtime for v0.1. Shells out to the docker CLI (present on dev machines/CI).
/// Applies resource limits and binds to loopback only. v0.1 requires a Dockerfile.
/// </summary>
public sealed class DockerRunner
{
    public static string ImageName(string slug, Guid deploymentId) =>
        $"octopus-{slug}:{deploymentId.ToString("N")[..12]}";

    public static string ContainerName(string slug) => $"octopus-{slug}";

    public async Task<Result<string>> BuildAsync(string contextDir, string imageName, Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(contextDir, "Dockerfile")))
            return Result<string>.Fail("v0.1 requires a Dockerfile at the repo root (dotnet buildpack is on the roadmap).");

        return await BuildImageAsync(contextDir, imageName, log, ct);
    }

    /// <summary>
    /// v0.2: builds from the repo Dockerfile or a buildpack-generated one.
    /// Generated Dockerfiles are written into the throwaway workspace only.
    /// </summary>
    public async Task<Result<string>> BuildWithPlanAsync(
        string contextDir, string imageName, BuildPlan plan, Action<string> log, CancellationToken ct)
    {
        if (plan is DotnetPlan dotnet)
        {
            log($"Buildpack: no Dockerfile — generating one for {dotnet.ProjectRelativePath} ({DockerfileGenerator.SdkImage}).");
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(contextDir, "Dockerfile"),
                    DockerfileGenerator.ForDotnet(dotnet.ProjectRelativePath, dotnet.AssemblyName), ct);
            }
            catch (Exception ex)
            {
                return Result<string>.Fail($"Cannot write generated Dockerfile: {ex.GetType().Name}.");
            }
        }

        return await BuildImageAsync(contextDir, imageName, log, ct);
    }

    private async Task<Result<string>> BuildImageAsync(string contextDir, string imageName, Action<string> log, CancellationToken ct)
    {
        log($"$ docker build -t {imageName} .");
        var run = await ProcessRunner.RunAsync("docker", $"build -t \"{imageName}\" .", contextDir, TimeSpan.FromMinutes(10), ct);
        AppendLog(log, run.StdOut);
        AppendLog(log, run.StdErr);
        if (run.TimedOut) return Result<string>.Fail("Docker build timed out.");
        return run.ExitCode == 0 ? Result<string>.Ok(imageName) : Result<string>.Fail($"Docker build failed (exit {run.ExitCode}).");
    }

    public async Task<Result<int>> StartAsync(string containerName, string imageName, int hostPort, int containerPort, Action<string> log, CancellationToken ct)
    {
        await StopAndRemoveAsync(containerName, log, ct);

        // Bind loopback only + resource limits. Container port defaults to 8080 (ASP.NET) but is overridable.
        var args = $"run -d --rm --name \"{containerName}\" -p 127.0.0.1:{hostPort}:{containerPort} --memory 512m --cpus 1.0 \"{imageName}\"";
        log("$ docker " + args);
        var run = await ProcessRunner.RunAsync("docker", args, Environment.CurrentDirectory, TimeSpan.FromMinutes(2), ct);
        AppendLog(log, run.StdOut);
        AppendLog(log, run.StdErr);
        if (run.TimedOut) return Result<int>.Fail("Docker run timed out.");
        return run.ExitCode == 0 ? Result<int>.Ok(hostPort) : Result<int>.Fail($"Docker run failed (exit {run.ExitCode}).");
    }

    public async Task StopAndRemoveAsync(string containerName, Action<string> log, CancellationToken ct)
    {
        var stop = await ProcessRunner.RunAsync("docker", $"stop \"{containerName}\"", Environment.CurrentDirectory, TimeSpan.FromSeconds(30), ct);
        if (stop.ExitCode == 0) log($"Stopped {containerName}.");
        var rm = await ProcessRunner.RunAsync("docker", $"rm -f \"{containerName}\"", Environment.CurrentDirectory, TimeSpan.FromSeconds(30), ct);
        if (rm.ExitCode == 0) log($"Removed {containerName}.");
    }

    private static void AppendLog(Action<string> log, string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(200))
            log(line.TrimEnd('\r').Length > 500 ? line[..500] : line.TrimEnd('\r'));
    }
}
