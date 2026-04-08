using Microsoft.AspNetCore.SignalR;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

/// <summary>
/// Base class for Wolverine enabled SignalR Hubs
/// </summary>
public class WolverineHub(SignalRTransport endpoint, IEnumerable<IWolverineHubHooks> hooks) : Hub
{
    [HubMethodName("ReceiveMessage")]
    public virtual Task ReceiveMessage(string json)
    {
        return endpoint.ReceiveAsync(Context, json);
    }
    
    public async Task JoinAsync(string group)
    {
        foreach (var hook in hooks)
        {
            await hook.OnJoinAsync(Context, group);
        }
    }

    public async Task LeaveAsync(string group)
    {
        foreach (var hook in hooks)
        {
            await hook.OnLeaveAsync(Context, group);
        }
    }
}

public interface IWolverineHubHooks
{
    ValueTask OnJoinAsync(HubCallerContext context, string group);
    ValueTask OnLeaveAsync(HubCallerContext context, string group);
}
