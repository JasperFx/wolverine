using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Interop;
using Wolverine.SignalR.Internals;
using Wolverine.Transports;

namespace Wolverine.SignalR;

/// <summary>
/// Base class for Wolverine enabled SignalR Hubs
/// </summary>
public abstract class WolverineHub : Hub
{
    private readonly SignalREndpoint _endpoint;

    protected WolverineHub(IWolverineRuntime runtime)
    {
        _endpoint = runtime.Options.SignalRTransport().HubEndpoints[GetType()];
    }

    public Task Receive(string json)
    {
        return _endpoint.ReceiveAsync(Context, this, json);
    }
}