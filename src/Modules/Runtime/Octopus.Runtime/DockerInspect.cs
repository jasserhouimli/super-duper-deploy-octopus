using System.Text.Json;
using Octopus.BuildingBlocks;

namespace Octopus.Runtime;

/// <summary>
/// Reads `docker inspect` state for a container. Pure parsers are unit-tested;
/// the docker call itself runs on hosts with the CLI available.
/// Images without a HEALTHCHECK have no Health block — then "running" plus the
/// TCP readiness probe (see <see cref="ContainerHealth"/>) is the health signal.
/// </summary>
public static class DockerInspect
{
    public sealed record ContainerState(bool Running, string? HealthStatus);

    /// <summary>Parses `docker inspect` JSON. Null when the payload is not understood.</summary>
    public static ContainerState? Parse(string inspectJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inspectJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;
            var state = doc.RootElement[0].GetProperty("State");
            var running = state.TryGetProperty("Running", out var r) && r.ValueKind == JsonValueKind.True;
            string? health = null;
            if (state.TryGetProperty("Health", out var h) && h.ValueKind == JsonValueKind.Object
                && h.TryGetProperty("Status", out var s) && s.ValueKind == JsonValueKind.String)
                health = s.GetString();
            return new ContainerState(running, health);
        }
        catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Healthy = running and (no healthcheck configured or status == healthy).</summary>
    public static bool IsHealthy(ContainerState state) =>
        state.Running && (string.IsNullOrEmpty(state.HealthStatus)
            || string.Equals(state.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase));

    public static async Task<Result<ContainerState?>> InspectAsync(string containerName, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync(
            "docker", $"inspect \"{containerName}\"", Environment.CurrentDirectory, TimeSpan.FromSeconds(30), ct);
        if (run.TimedOut) return Result<ContainerState?>.Fail("Docker inspect timed out.");
        if (run.ExitCode != 0) return Result<ContainerState?>.Fail($"Docker inspect failed (exit {run.ExitCode}).");
        return Result<ContainerState?>.Ok(Parse(run.StdOut));
    }
}
