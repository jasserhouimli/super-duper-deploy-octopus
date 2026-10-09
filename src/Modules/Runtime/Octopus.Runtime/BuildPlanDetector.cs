using Octopus.BuildingBlocks;

namespace Octopus.Runtime;

/// <summary>
/// Picks a build plan for a cloned repo. Repo Dockerfile always wins;
/// otherwise the shallowest Web-SDK project is used (override allowed).
/// Pure filesystem logic — unit tested, no docker required.
/// </summary>
public static class BuildPlanDetector
{
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", "artifacts", "TestResults",
    };

    public static Result<BuildPlan> Detect(string contextDir, string? projectOverride = null, string? dockerfileOverride = null)
    {
        if (string.IsNullOrWhiteSpace(contextDir) || !Directory.Exists(contextDir))
            return Result<BuildPlan>.Fail("Workspace directory is missing.");

        // Explicit Dockerfile (monorepo layouts) wins over everything.
        if (!string.IsNullOrWhiteSpace(dockerfileOverride))
        {
            var norm = NormalizeDockerfilePath(dockerfileOverride);
            if (!norm.IsSuccess) return Result<BuildPlan>.Fail(norm.Error);
            if (!File.Exists(Path.Combine(contextDir, norm.Value!)))
                return Result<BuildPlan>.Fail($"Dockerfile '{norm.Value}' not found in repo.");
            return Result<BuildPlan>.Ok(new DockerfilePlan(norm.Value));
        }

        if (File.Exists(Path.Combine(contextDir, "Dockerfile"))
            || File.Exists(Path.Combine(contextDir, "dockerfile")))
            return Result<BuildPlan>.Ok(new DockerfilePlan(null));

        string relative;
        if (!string.IsNullOrWhiteSpace(projectOverride))
        {
            var norm = NormalizeProjectPath(projectOverride);
            if (!norm.IsSuccess) return Result<BuildPlan>.Fail(norm.Error);
            relative = norm.Value!;
            if (!File.Exists(Path.Combine(contextDir, relative)))
                return Result<BuildPlan>.Fail($"Project '{relative}' not found in repo.");
        }
        else
        {
            var found = FindWebProject(contextDir);
            if (!found.IsSuccess) return Result<BuildPlan>.Fail(found.Error);
            relative = found.Value!;
        }

        return Result<BuildPlan>.Ok(new DotnetPlan(relative, Path.GetFileNameWithoutExtension(relative)));
    }

    public static Result<string> NormalizeProjectPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 300)
            return Result<string>.Fail("ProjectPath must be a relative .csproj path (max 300 chars).");
        var rel = input.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(rel) || rel.StartsWith('/') || rel.Contains("../", StringComparison.Ordinal) || rel == ".." || rel.EndsWith("/..", StringComparison.Ordinal))
            return Result<string>.Fail("ProjectPath must be a relative path without '..'.");
        if (!rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return Result<string>.Fail("ProjectPath must point at a .csproj file.");
        return Result<string>.Ok(rel);
    }

    /// <summary>Validates an explicit Dockerfile location (monorepo layouts). Existence is checked by Detect.</summary>
    public static Result<string> NormalizeDockerfilePath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 300)
            return Result<string>.Fail("DockerfilePath must be a relative path (max 300 chars).");
        var rel = input.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(rel) || rel.StartsWith('/') || rel.Contains("../", StringComparison.Ordinal) || rel == ".." || rel.EndsWith("/..", StringComparison.Ordinal))
            return Result<string>.Fail("DockerfilePath must be a relative path without '..'.");
        if (rel.EndsWith('/') || rel.Length == 0)
            return Result<string>.Fail("DockerfilePath must point at a file.");
        return Result<string>.Ok(rel);
    }

    internal static Result<string> FindWebProject(string root)
    {
        List<string> projects;
        try
        {
            projects = EnumerateCsproj(root, 0).Take(500).ToList();
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Cannot scan workspace: {ex.GetType().Name}.");
        }

        if (projects.Count == 0)
            return Result<string>.Fail("No Dockerfile and no .csproj found — add a Dockerfile or a .NET project.");

        foreach (var file in projects.OrderBy(f => f.Length))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (text.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                return Result<string>.Ok(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        return Result<string>.Fail(
            $"Found {projects.Count} .csproj file(s) but none uses Microsoft.NET.Sdk.Web. " +
            "Set ProjectPath or add a Dockerfile.");
    }

    private static IEnumerable<string> EnumerateCsproj(string dir, int depth)
    {
        if (depth > 4) yield break;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { yield break; }
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            bool isDir;
            try { isDir = Directory.Exists(entry); }
            catch { continue; }
            if (isDir)
            {
                if (ExcludedDirs.Contains(name)) continue;
                foreach (var f in EnumerateCsproj(entry, depth + 1)) yield return f;
            }
            else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }
}
