using Microsoft.AspNetCore.SignalR;
using Wolverine.SignalR.Internals;

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
    public virtual Task ReceiveMessage(string json)
    {
        return _endpoint.ReceiveAsync(Context, json);
    }
}