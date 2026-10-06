using Octopus.Deployments;

namespace Octopus.Tests;

public sealed class DeploymentActionTests
{
    [Fact]
    public void Cancel_moves_queued_to_cancelled()
    {
        var d = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = DeploymentStatus.Queued };
        var now = DateTimeOffset.UtcNow;
        var r = DeploymentActions.Cancel(d, now);
        Assert.True(r.IsSuccess);
        Assert.Equal(DeploymentStatus.Cancelled, d.Status);
        Assert.Equal(now, d.FinishedAt);
    }

    [Theory]
    [InlineData(DeploymentStatus.Cloning)]
    [InlineData(DeploymentStatus.Building)]
    [InlineData(DeploymentStatus.Starting)]
    [InlineData(DeploymentStatus.Running)]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Stopped)]
    [InlineData(DeploymentStatus.Cancelled)]
    public void Cancel_rejects_non_queued(DeploymentStatus status)
    {
        var d = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = status };
        var r = DeploymentActions.Cancel(d, DateTimeOffset.UtcNow);
        Assert.False(r.IsSuccess);
        Assert.Equal(status, d.Status);
        Assert.Null(d.FinishedAt);
    }
}
