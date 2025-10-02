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
public class WolverineHub : Hub
{
    private readonly SignalRTransport _endpoint;

    public WolverineHub(SignalRTransport endpoint)
    {
        _endpoint = endpoint;
    }

    [HubMethodName("ReceiveMessage")]
    public Task ReceiveMessage(string json)
    {
        return _endpoint.ReceiveAsync(Context, json);
    }
}