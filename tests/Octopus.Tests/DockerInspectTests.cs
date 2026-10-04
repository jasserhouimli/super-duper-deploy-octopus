using Octopus.Runtime;

namespace Octopus.Tests;

public sealed class DockerInspectTests
{
    [Fact]
    public void Parses_running_without_healthcheck()
    {
        var state = DockerInspect.Parse("""[{"State": {"Running": true}}]""");
        Assert.NotNull(state);
        Assert.True(state!.Running);
        Assert.Null(state.HealthStatus);
        Assert.True(DockerInspect.IsHealthy(state));
    }

    [Fact]
    public void Parses_healthy_status()
    {
        var state = DockerInspect.Parse("""[{"State": {"Running": true, "Health": {"Status": "healthy"}}}]""");
        Assert.NotNull(state);
        Assert.True(DockerInspect.IsHealthy(state!));
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("unhealthy")]
    public void Starting_or_unhealthy_is_not_healthy(string status)
    {
        var real = DockerInspect.Parse("""[{"State": {"Running": true, "Health": {"Status": "__S__"}}}]""".Replace("__S__", status));
        Assert.NotNull(real);
        Assert.False(DockerInspect.IsHealthy(real!));
    }

    [Fact]
    public void Stopped_container_is_not_healthy()
    {
        var state = DockerInspect.Parse("""[{"State": {"Running": false, "Health": {"Status": "healthy"}}}]""");
        Assert.NotNull(state);
        Assert.False(DockerInspect.IsHealthy(state!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    public void Malformed_payload_returns_null(string payload) =>
        Assert.Null(DockerInspect.Parse(payload));
}
