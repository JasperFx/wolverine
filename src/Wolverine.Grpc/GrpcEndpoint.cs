using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Grpc.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Grpc;

public class GrpcEndpoint : Endpoint
{
    public GrpcEndpoint(Uri uri) : base(uri, EndpointRole.Application)
    {
        Host = uri.Host;
        Port = uri.IsDefaultPort ? 5000 : uri.Port;
    }

    public string Host { get; }
    public int Port { get; }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var listener = new GrpcListener(Uri, Port, receiver, runtime.LoggerFactory.CreateLogger<GrpcListener>());
        await listener.StartAsync();
        return listener;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new GrpcSender(Uri, Host, Port, runtime.LoggerFactory.CreateLogger<GrpcSender>());
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode is EndpointMode.Inline or EndpointMode.BufferedInMemory;
    }
}
