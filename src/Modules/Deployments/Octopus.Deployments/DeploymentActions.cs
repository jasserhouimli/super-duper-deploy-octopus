using Octopus.BuildingBlocks;

namespace Octopus.Deployments;

/// <summary>
/// Explicit deployment state transitions. Endpoints call these so the rules
/// stay unit-testable; only the listed transitions are allowed.
/// </summary>
public static class DeploymentActions
{
    /// <summary>
    /// Cancels a Queued deployment. Active (claimed) deployments cannot be
    /// cancelled in v1 — the worker owns them once claimed.
    /// </summary>
    public static Result<string> Cancel(Deployment deployment, DateTimeOffset now)
    {
        if (deployment.Status != DeploymentStatus.Queued)
            return Result<string>.Fail($"Only queued deployments can be cancelled (current: {deployment.Status}).");
        deployment.Status = DeploymentStatus.Cancelled;
        deployment.FinishedAt = now;
        return Result<string>.Ok("Cancelled by user.");
    }

    /// <summary>
    /// Re-queues a Failed or Cancelled deployment as a fresh attempt on the
    /// same record: attempt history stays in the log tail, prior outcome fields
    /// are cleared for the next run.
    /// </summary>
    public static Result<string> Retry(Deployment deployment, DateTimeOffset now)
    {
        if (deployment.Status is not (DeploymentStatus.Failed or DeploymentStatus.Cancelled))
            return Result<string>.Fail($"Only failed or cancelled deployments can be retried (current: {deployment.Status}).");
        deployment.Status = DeploymentStatus.Queued;
        deployment.Error = null;
        deployment.CommitSha = null;
        deployment.StartedAt = null;
        deployment.FinishedAt = null;
        return Result<string>.Ok($"Requeued for retry at {now:O}.");
    }
}
