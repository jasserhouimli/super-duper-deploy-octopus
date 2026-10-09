using Octopus.Runtime;

namespace Octopus.Tests;

public sealed class BuildpackTests : IDisposable
{
    private readonly List<string> _dirs = [];

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "octo-bp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Dockerfile_wins()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "Dockerfile"), "FROM scratch\n");
        var r = BuildPlanDetector.Detect(dir);
        Assert.True(r.IsSuccess);
        Assert.IsType<DockerfilePlan>(r.Value);
    }

    [Fact]
    public void Detects_shallowest_web_project()
    {
        var dir = NewDir();
        var webDir = Path.Combine(dir, "src", "Web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "Web.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var libDir = Path.Combine(dir, "Lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "Lib.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");

        var r = BuildPlanDetector.Detect(dir);
        Assert.True(r.IsSuccess);
        var plan = Assert.IsType<DotnetPlan>(r.Value);
        Assert.Equal("src/Web/Web.csproj", plan.ProjectRelativePath);
        Assert.Equal("Web", plan.AssemblyName);
    }

    [Fact]
    public void Empty_dir_fails_with_helpful_message()
    {
        var r = BuildPlanDetector.Detect(NewDir());
        Assert.False(r.IsSuccess);
        Assert.Contains("Dockerfile", r.Error);
    }

    [Fact]
    public void Non_web_only_fails()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "Tool.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""");
        var r = BuildPlanDetector.Detect(dir);
        Assert.False(r.IsSuccess);
        Assert.Contains("Sdk.Web", r.Error);
    }

    [Theory]
    [InlineData("../evil/Evil.csproj", false)]
    [InlineData("/abs/Evil.csproj", false)]
    [InlineData("src/Web.txt", false)]
    [InlineData("src/Web/Web.csproj", true)]
    public void ProjectPath_override_is_validated(string input, bool ok)
    {
        var dir = NewDir();
        if (ok)
        {
            Directory.CreateDirectory(Path.Combine(dir, "src", "Web"));
            File.WriteAllText(Path.Combine(dir, "src", "Web", "Web.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        }
        var r = BuildPlanDetector.Detect(dir, input);
        Assert.Equal(ok, r.IsSuccess);
    }

    [Fact]
    public void Generator_produces_valid_shape()
    {
        var df = DockerfileGenerator.ForDotnet("src/Web/Web.csproj", "Web");
        Assert.Contains(DockerfileGenerator.SdkImage, df);
        Assert.Contains(DockerfileGenerator.RuntimeImage, df);
        Assert.Contains("src/Web/Web.csproj", df);
        Assert.Contains("\"Web.dll\"", df);
        Assert.Contains("8080", df);
    }

    [Fact]
    public void Nested_dockerfile_override_is_detected()
    {
        var dir = NewDir();
        var sub = Path.Combine(dir, "deploy", "prod");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "Dockerfile"), "FROM scratch\n");
        var r = BuildPlanDetector.Detect(dir, dockerfileOverride: "deploy/prod/Dockerfile");
        Assert.True(r.IsSuccess);
        var plan = Assert.IsType<DockerfilePlan>(r.Value);
        Assert.Equal("deploy/prod/Dockerfile", plan.DockerfileRelativePath);
    }

    [Fact]
    public void Dockerfile_override_beats_root_dockerfile()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "Dockerfile"), "FROM scratch\n");
        var sub = Path.Combine(dir, "alt");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "Dockerfile"), "FROM scratch\n");
        var r = BuildPlanDetector.Detect(dir, dockerfileOverride: "alt/Dockerfile");
        Assert.True(r.IsSuccess);
        Assert.Equal("alt/Dockerfile", Assert.IsType<DockerfilePlan>(r.Value).DockerfileRelativePath);
    }

    [Theory]
    [InlineData("../evil/Dockerfile")]
    [InlineData("/abs/Dockerfile")]
    [InlineData("missing/Dockerfile")]
    public void Bad_dockerfile_overrides_fail(string input)
    {
        Assert.False(BuildPlanDetector.Detect(NewDir(), dockerfileOverride: input).IsSuccess);
    }

    [Fact]
    public void Build_args_use_dash_f_only_for_explicit_path()
    {
        Assert.Equal("build -t \"img\" .", DockerRunner.BuildBuildArgs("img", null));
        Assert.Equal("build -f \"deploy/prod/Dockerfile\" -t \"img\" .",
            DockerRunner.BuildBuildArgs("img", "deploy/prod/Dockerfile"));
    }
}
