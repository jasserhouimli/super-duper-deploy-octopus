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
}

public sealed class DeploymentLog
{
    public long Id { get; set; }
    public Guid DeploymentId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Line { get; set; } = string.Empty;
}
