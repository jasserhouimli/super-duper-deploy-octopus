using Octopus.BuildingBlocks;

namespace Octopus.Apps;

/// <summary>
/// Per-app resource quotas. Small allowlist keeps scheduling predictable and
/// prevents a single app from starving the host. Values map to docker
/// `--memory` / `--cpus` in Runtime (see DockerRunner).
/// </summary>
public static class AppQuotas
{
    public const int DefaultMemoryMb = 512;
    public const int DefaultCpuMillicores = 1000;

    public static readonly int[] AllowedMemoryMb = [128, 256, 512, 1024, 2048];
    public static readonly int[] AllowedCpuMillicores = [250, 500, 1000, 2000];

    public static Result<(int MemoryMb, int CpuMillicores)> Validate(int? memoryMb, int? cpuMillicores)
    {
        var mem = memoryMb ?? DefaultMemoryMb;
        var cpu = cpuMillicores ?? DefaultCpuMillicores;
        if (!AllowedMemoryMb.Contains(mem))
            return Result<(int, int)>.Fail($"Memory must be one of: {string.Join(", ", AllowedMemoryMb)} MB.");
        if (!AllowedCpuMillicores.Contains(cpu))
            return Result<(int, int)>.Fail($"CPU must be one of: {string.Join(", ", AllowedCpuMillicores)} millicores.");
        return Result<(int, int)>.Ok((mem, cpu));
    }

    /// <summary>Docker --memory flag for a validated quota.</summary>
    public static string ToDockerMemory(int memoryMb) => $"{memoryMb}m";

    /// <summary>Docker --cpus flag for a validated quota (millicores -> cores).</summary>
    public static string ToDockerCpus(int cpuMillicores) =>
        (cpuMillicores / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
