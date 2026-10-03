namespace Octopus.Runtime;

/// <summary>How a cloned repo becomes a container image.</summary>
public abstract record BuildPlan;

/// <summary>Repo provides its own Dockerfile at the root (authoritative).</summary>
public sealed record DockerfilePlan : BuildPlan;

/// <summary>
/// No Dockerfile: Octopus generates one for a .NET web project.
/// Path is relative to the clone root, using '/' separators.
/// </summary>
public sealed record DotnetPlan(string ProjectRelativePath, string AssemblyName) : BuildPlan;
