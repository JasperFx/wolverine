using System.Net;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Tcp;

public class TcpEndpoint : Endpoint
{
    public TcpEndpoint() : this("localhost", 2000)
    {
    }

    public TcpEndpoint(int port) : this("localhost", port)
    {
    }

    public TcpEndpoint(string hostName, int port) : base(ToUri(port, hostName), EndpointRole.Application)
    {
        HostName = hostName;
        Port = port;

        // ReSharper disable once VirtualMemberCallInConstructor
        EndpointName = Uri.ToString();
    }

    public string HostName { get; }

    public int Port { get; }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode != EndpointMode.Inline;
    }

    public static Uri ToUri(int port, string hostName = "localhost")
    {
        return $"tcp://{hostName}:{port}".ToUri();
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        // check the uri for an ip address to bind to
        var cancellation = runtime.DurabilitySettings.Cancellation;

        var hostNameType = Uri.CheckHostName(HostName);

        var logger = runtime.LoggerFactory.CreateLogger<TcpEndpoint>();

        if (hostNameType != UriHostNameType.IPv4 && hostNameType != UriHostNameType.IPv6)
        {
            var listener = HostName == "localhost"
                ? new SocketListener(this, receiver, logger, IPAddress.Loopback, Port, cancellation)
                : new SocketListener(this, receiver, logger, IPAddress.Any, Port, cancellation);

            return ValueTask.FromResult<IListener>(listener);
        }

        var ipaddr = IPAddress.Parse(HostName);
        return ValueTask.FromResult<IListener>(new SocketListener(this, receiver, logger, ipaddr, Port,
            cancellation));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new BatchedSender(this, new SocketSenderProtocol(), runtime.DurabilitySettings.Cancellation,
            runtime.LoggerFactory.CreateLogger<SocketSenderProtocol>());
    }
}