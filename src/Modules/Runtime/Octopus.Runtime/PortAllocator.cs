using System.Net;
using System.Net.Sockets;

namespace Octopus.Runtime;

/// <summary>
/// Allocates loopback ports from a fixed range so deployed apps cannot squat privileged ports.
/// </summary>
public static class PortAllocator
{
    public const int MinPort = 5100;
    public const int MaxPort = 5999;

    public static int FindFreePort(HashSet<int> taken)
    {
        for (var port = MinPort; port <= MaxPort; port++)
        {
            if (taken.Contains(port)) continue;
            if (IsFree(port)) return port;
        }
        throw new InvalidOperationException("No free ports available in range 5100-5999.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
