namespace Octopus.Apps;

/// <summary>
/// Owns developer-facing deployable units.
/// An App references a GitHub repo; it does NOT own build/execution (see Deployments/Runtime).
/// </summary>
public sealed class App
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string RepoUrl { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public AppStatus Status { get; set; } = AppStatus.Created;
    public int TargetPort { get; set; }
    public string? ContainerName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AppStatus
{
    Created = 0,
    Deploying = 1,
    Running = 2,
    Stopped = 3,
    Failed = 4,
}
