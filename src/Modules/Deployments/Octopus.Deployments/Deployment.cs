namespace Octopus.Deployments;

public sealed class Deployment
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public string? CommitSha { get; set; }
    public string? Error { get; set; }
    public int ContainerPort { get; set; } = 8080;
    /// <summary>Optional override for the dotnet buildpack (relative .csproj path). Null = auto-detect.</summary>
    public string? ProjectPath { get; set; }
    /// <summary>Optional override for monorepos (relative Dockerfile path). Null = root Dockerfile or buildpack.</summary>
    public string? DockerfilePath { get; set; }
    /// <summary>Worker holding the claim (machine:pid), null when unclaimed.</summary>
    public string? LeaseOwner { get; set; }
    /// <summary>Claim expiry, renewed by heartbeat. Concurrency token: a lost race throws instead of double-claiming.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public enum DeploymentStatus
{
    Queued = 0,
    Cloning = 1,
    Building = 2,
    Starting = 3,
    Running = 4,
    Failed = 5,
    Stopped = 6,
    Cancelled = 7,
}

public sealed class DeploymentLog
{
    public long Id { get; set; }
    public Guid DeploymentId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Line { get; set; } = string.Empty;
}
