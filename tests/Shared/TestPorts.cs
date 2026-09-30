using System.Net;
using System.Net.Sockets;

namespace redb.Route.Tests.Shared;

/// <summary>
/// Ports for test servers. The old recipe — bind port 0, read the number, close, let Kestrel bind it later —
/// took the number from the ephemeral range (Windows 49152–65535, Linux 32768–60999), the same range the OS
/// hands out for the local end of every outgoing client connection. Between the close and Kestrel's bind the
/// port could go to a client socket of a test running in parallel (three TFMs and several projects run at
/// once), and the server failed with "address already in use".
/// <para>
/// Here the ports come from 20000–32767, below both ephemeral ranges, so the OS never gives them to a client
/// socket. Each process starts at its own random point and walks forward, so parallel processes rarely meet,
/// and every port is checked by binding it before it is handed out (a port reserved by Hyper-V or held by a
/// container fails that check and is skipped). A number is never handed out twice in one process.
/// </para>
/// </summary>
internal static class TestPorts
{
    private const int First = 20000;
    private const int Last = 32767;
    private const int Span = Last - First + 1;

    private static readonly int Start = Random.Shared.Next(Span);
    private static int _taken = -1;

    /// <summary>A port nothing listens on at the moment, outside the ephemeral range.</summary>
    public static int Next()
    {
        for (var attempt = 0; attempt < Span; attempt++)
        {
            var port = First + (Start + Interlocked.Increment(ref _taken)) % Span;
            if (IsFree(port))
                return port;
        }
        throw new InvalidOperationException($"No free port left in {First}-{Last} for this test process.");
    }

    private static bool IsFree(int port)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Windows otherwise lets a wildcard bind share a port someone holds on a specific address.
        if (OperatingSystem.IsWindows())
            probe.ExclusiveAddressUse = true;
        try
        {
            // Any address: a test server may bind 0.0.0.0 as well as 127.0.0.1.
            probe.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            // In use, or excluded by the OS: this port is not the answer, the next one is tried.
            return false;
        }
    }
}
