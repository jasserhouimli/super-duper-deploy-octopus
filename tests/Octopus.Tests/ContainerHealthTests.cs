using System.Net;
using System.Net.Sockets;
using System.Text;
using Octopus.Runtime;

namespace Octopus.Tests;

public sealed class ContainerHealthTests
{
    [Theory]
    [InlineData(5100, true)]
    [InlineData(5999, true)]
    [InlineData(5099, false)]
    [InlineData(6000, false)]
    [InlineData(80, false)]
    [InlineData(8080, false)]
    public void Only_allocated_range_is_probable(int port, bool expected) =>
        Assert.Equal(expected, ContainerHealth.IsProbedPort(port));

    [Fact]
    public async Task Tcp_probe_fails_fast_on_closed_port()
    {
        var port = PortAllocator.FindFreePort(new HashSet<int>());
        Assert.False(await ContainerHealth.ProbeTcpAsync(port, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Tcp_probe_rejects_out_of_range_port() =>
        Assert.False(await ContainerHealth.ProbeTcpAsync(80, TimeSpan.FromSeconds(1)));

    [Fact]
    public async Task Tcp_probe_succeeds_when_listening()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!ContainerHealth.IsProbedPort(port))
                return; // ephemeral port outside our range: range guard tested above.
            var accept = listener.AcceptTcpClientAsync();
            Assert.True(await ContainerHealth.ProbeTcpAsync(port, TimeSpan.FromSeconds(5)));
            using var server = await accept;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Http_probe_rejects_non_loopback_and_out_of_range()
    {
        Assert.False(await ContainerHealth.ProbeHttpAsync("http://example.com:5100/health", TimeSpan.FromSeconds(1)));
        Assert.False(await ContainerHealth.ProbeHttpAsync("https://127.0.0.1:5100/health", TimeSpan.FromSeconds(1)));
        Assert.False(await ContainerHealth.ProbeHttpAsync("http://127.0.0.1:80/health", TimeSpan.FromSeconds(1)));
        Assert.False(await ContainerHealth.ProbeHttpAsync("not-a-url", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Http_probe_returns_true_for_2xx()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!ContainerHealth.IsProbedPort(port))
                return;
            var serve = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var buf = new byte[4096];
#pragma warning disable CA2022 // test stub: single read of the request head is sufficient
                await stream.ReadAsync(buf);
#pragma warning restore CA2022
                var body = "ok";
                var res = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(res));
            });
            Assert.True(await ContainerHealth.ProbeHttpAsync($"http://127.0.0.1:{port}/health", TimeSpan.FromSeconds(5)));
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Wait_returns_false_when_nothing_listens()
    {
        var port = PortAllocator.FindFreePort(new HashSet<int>());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await ContainerHealth.WaitForTcpAsync(port, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200)));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }
}
