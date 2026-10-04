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
        => await StartWithEnvAsync(containerName, imageName, hostPort, containerPort, null, log, ct);

    /// <summary>
    /// Starts a container, injecting env vars via a throwaway --env-file so values
    /// never appear in process arguments or logs. Only key names/count are logged.
    /// </summary>
    public async Task<Result<int>> StartWithEnvAsync(
        string containerName,
        string imageName,
        int hostPort,
        int containerPort,
        IReadOnlyDictionary<string, string>? envVars,
        Action<string> log,
        CancellationToken ct)
    {
        await StopAndRemoveAsync(containerName, log, ct);

        string? envFile = null;
        try
        {
            if (envVars is { Count: > 0 })
            {
                envFile = await WriteEnvFileAsync(envVars, ct);
                log($"Injecting {envVars.Count} env var(s): [{string.Join(",", envVars.Keys.OrderBy(k => k))}] (values redacted).");
            }

            // Bind loopback only + resource limits. Container port defaults to 8080 (ASP.NET) but is overridable.
            var args = BuildStartArgs(containerName, imageName, hostPort, containerPort, envFile);
            log("$ docker " + RedactForLog(args));
            var run = await ProcessRunner.RunAsync("docker", args, Environment.CurrentDirectory, TimeSpan.FromMinutes(2), ct);
            AppendLog(log, run.StdOut);
            AppendLog(log, run.StdErr);
            if (run.TimedOut) return Result<int>.Fail("Docker run timed out.");
            return run.ExitCode == 0 ? Result<int>.Ok(hostPort) : Result<int>.Fail($"Docker run failed (exit {run.ExitCode}).");
        }
        finally
        {
            if (envFile is not null)
            {
                try { File.Delete(envFile); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Builds `docker run` args. Env values are never embedded — only an --env-file path.</summary>
    public static string BuildStartArgs(string containerName, string imageName, int hostPort, int containerPort, string? envFilePath)
    {
        var envPart = envFilePath is null ? string.Empty : $" --env-file \"{envFilePath}\"";
        return $"run -d --rm --name \"{containerName}\"{envPart} -p 127.0.0.1:{hostPort}:{containerPort} --memory 512m --cpus 1.0 \"{imageName}\"";
    }

    /// <summary>Serializes validated env vars to docker --env-file format (KEY=VALUE per line).</summary>
    public static string BuildEnvFileContent(IReadOnlyDictionary<string, string> envVars)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (k, v) in envVars.OrderBy(e => e.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v).Append('\n');
        return sb.ToString();
    }

    internal static async Task<string> WriteEnvFileAsync(IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"octopus-env-{Guid.NewGuid():N}.env");
        await File.WriteAllTextAsync(path, BuildEnvFileContent(envVars), ct);
        return path;
    }

    internal static string RedactForLog(string args) => args; // args never contain values (env-file path only)

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
