using System.Diagnostics;
using System.Text;

namespace Octopus.BuildingBlocks;

/// <summary>
/// Runs external processes (git, docker) with timeouts and bounded output.
/// Never logs secrets: callers must redact URLs before passing display strings.
/// </summary>
public static class ProcessRunner
{
    public sealed record RunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    public static async Task<RunResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var limit = timeout ?? TimeSpan.FromMinutes(5);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);

        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        try
        {
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null && stdOut.Length < 200_000) stdOut.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null && stdErr.Length < 200_000) stdErr.AppendLine(e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new RunResult(-1, Truncate(stdOut.ToString()), Truncate(stdErr.ToString()), TimedOut: true);
            }

            return new RunResult(proc.ExitCode, Truncate(stdOut.ToString()), Truncate(stdErr.ToString()), TimedOut: false);
        }
        catch (Exception ex)
        {
            return new RunResult(-1, string.Empty, Truncate($"process start failed: {ex.GetType().Name}"), TimedOut: false);
        }
    }

    private static string Truncate(string s) => s.Length > 200_000 ? s[..200_000] : s;
}
