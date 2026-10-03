using Octopus.BuildingBlocks;

namespace Octopus.GitHub;

/// <summary>
/// Clones a repo shallowly with a bounded timeout. Logs use redacted owner/repo only.
/// </summary>
public static class GitCloner
{
    public static async Task<Result<string>> CloneAsync(
        string canonicalUrl,
        string owner,
        string repo,
        string branch,
        string destinationDir,
        CancellationToken ct)
    {
        try
        {
            if (Directory.Exists(destinationDir))
                Directory.Delete(destinationDir, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationDir)!);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Cannot prepare workspace: {ex.GetType().Name}.");
        }

        // Quote args; URL was validated to be a github.com https URL.
        var args = $"clone --depth 1 --branch \"{branch}\" \"{canonicalUrl}\" \"{destinationDir}\"";
        var run = await ProcessRunner.RunAsync("git", args, Environment.CurrentDirectory, TimeSpan.FromMinutes(2), ct);

        if (run.TimedOut)
            return Result<string>.Fail($"Clone of {owner}/{repo} timed out.");
        if (run.ExitCode != 0)
        {
            var hint = run.StdErr.Length > 500 ? run.StdErr[..500] : run.StdErr;
            return Result<string>.Fail($"Clone of {owner}/{repo} failed (exit {run.ExitCode}). {hint}".Trim());
        }

        var sha = await ResolveShaAsync(destinationDir, ct);
        return Result<string>.Ok(sha);
    }

    private static async Task<string> ResolveShaAsync(string dir, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync("git", "rev-parse --short HEAD", dir, TimeSpan.FromSeconds(15), ct);
        return run.ExitCode == 0 ? run.StdOut.Trim() : "unknown";
    }
}
