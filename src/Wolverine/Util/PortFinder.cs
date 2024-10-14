using System.Net;
using System.Net.Sockets;

namespace Wolverine.Util;

public class PortFinder
{
    private static readonly IPEndPoint DefaultLoopbackEndpoint = new(IPAddress.Loopback, 0);

    public static int GetAvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(DefaultLoopbackEndpoint);
        var port = ((IPEndPoint)socket.LocalEndPoint).Port;
        return port;
    }
}