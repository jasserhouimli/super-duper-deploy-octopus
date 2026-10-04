using System.Net.Sockets;

namespace Octopus.Runtime;

/// <summary>
/// Loopback-only readiness probes for freshly started app containers.
/// Probes 127.0.0.1 inside the allocated host-port range (5100-5999) so there
/// is no SSRF surface: the host and port range are fixed by the platform.
/// HTTP probing is a best-effort convenience on top of the generic TCP check.
/// </summary>
public static class ContainerHealth
{
    public static bool IsProbedPort(int port) =>
        port is >= PortAllocator.MinPort and <= PortAllocator.MaxPort;

    /// <summary>True when something accepts TCP on 127.0.0.1:port within timeout.</summary>
    public static async Task<bool> ProbeTcpAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsProbedPort(port)) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when GET url returns 2xx within timeout. Only http://127.0.0.1 URLs are probed.</summary>
    public static async Task<bool> ProbeHttpAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!TryParseLoopbackUrl(url, out var uri) || uri is null) return false;
        if (!IsProbedPort(uri.Port)) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            using var res = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Waits until the TCP port accepts connections or the deadline passes.</summary>
    public static async Task<bool> WaitForTcpAsync(
        int port,
        TimeSpan perAttempt,
        TimeSpan overall,
        TimeSpan? delayBetween = null,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + overall;
        var delay = delayBetween ?? TimeSpan.FromSeconds(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ProbeTcpAsync(port, perAttempt, ct)) return true;
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return await ProbeTcpAsync(port, perAttempt, ct);
    }

    internal static bool TryParseLoopbackUrl(string url, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (!string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(u.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
        uri = u;
        return true;
    }
}
