using System.Xml.Linq;

namespace Octopus.Tests;

/// <summary>
/// Guards the modular-monolith dependency directions: modules may only depend
/// inward (BuildingBlocks) or on explicitly allowed peers; nothing references
/// the Api/Worker composition roots. Runs in `dotnet test`, locally and in CI.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Octopus.BuildingBlocks"] = [],
        ["Octopus.GitHub"] = ["Octopus.BuildingBlocks"],
        ["Octopus.Runtime"] = ["Octopus.BuildingBlocks"],
        ["Octopus.Apps"] = ["Octopus.BuildingBlocks", "Octopus.GitHub"],
        ["Octopus.Routing"] = ["Octopus.BuildingBlocks", "Octopus.Apps"],
        ["Octopus.Deployments"] = ["Octopus.BuildingBlocks", "Octopus.Apps", "Octopus.Runtime", "Octopus.GitHub"],
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Octopus.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repo root (Octopus.slnx) not found.");
    }

    private static Dictionary<string, string[]> ReferenceMap()
    {
        var root = RepoRoot();
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            var doc = XDocument.Load(csproj);
            var refs = doc.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v is not null)
                .Select(v => Path.GetFileNameWithoutExtension(v!.Replace('\\', '/')))
                .ToList();
            map[name] = refs.ToArray();
        }
        return map;
    }

    [Fact]
    public void Modules_only_reference_allowed_peers()
    {
        var map = ReferenceMap();
        foreach (var (project, allowed) in Allowed)
        {
            Assert.True(map.ContainsKey(project), $"Project {project} not found.");
            var illegal = map[project].Except(allowed, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(illegal.Count == 0,
                $"{project} references disallowed project(s): {string.Join(", ", illegal)}. " +
                $"Allowed: {(allowed.Length == 0 ? "(none)" : string.Join(", ", allowed))}.");
        }
    }

    [Fact]
    public void Nothing_references_composition_roots()
    {
        var map = ReferenceMap();
        foreach (var (project, refs) in map)
        {
            Assert.False(refs.Contains("Octopus.Api", StringComparer.OrdinalIgnoreCase),
                $"{project} must not reference Octopus.Api (composition root).");
            Assert.False(refs.Contains("Octopus.Worker", StringComparer.OrdinalIgnoreCase),
                $"{project} must not reference Octopus.Worker (composition root).");
        }
    }
}
