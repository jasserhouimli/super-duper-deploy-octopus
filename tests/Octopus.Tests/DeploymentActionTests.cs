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

    [Theory]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Cancelled)]
    public void Retry_requeues_terminal_attempts(DeploymentStatus status)
    {
        var d = new Deployment
        {
            Id = Guid.NewGuid(),
            AppId = Guid.NewGuid(),
            Status = status,
            Error = "boom",
            CommitSha = "abc",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishedAt = DateTimeOffset.UtcNow,
        };
        var r = DeploymentActions.Retry(d, DateTimeOffset.UtcNow);
        Assert.True(r.IsSuccess);
        Assert.Equal(DeploymentStatus.Queued, d.Status);
        Assert.Null(d.Error);
        Assert.Null(d.CommitSha);
        Assert.Null(d.StartedAt);
        Assert.Null(d.FinishedAt);
    }

    [Theory]
    [InlineData(DeploymentStatus.Queued)]
    [InlineData(DeploymentStatus.Cloning)]
    [InlineData(DeploymentStatus.Building)]
    [InlineData(DeploymentStatus.Starting)]
    [InlineData(DeploymentStatus.Running)]
    [InlineData(DeploymentStatus.Stopped)]
    public void Retry_rejects_non_retryable(DeploymentStatus status)
    {
        var d = new Deployment { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Status = status, Error = "x" };
        var r = DeploymentActions.Retry(d, DateTimeOffset.UtcNow);
        Assert.False(r.IsSuccess);
        Assert.Equal(status, d.Status);
    }
}
