using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

/// <summary>
/// Side Effect that will remove the SignalR connection that received the original
/// message from a named SignalR group on the originating Hub
/// </summary>
/// <param name="GroupName"></param>
public record RemoveConnectionToGroup(string GroupName) : ISignalRAction
{
    public async ValueTask ExecuteAsync(IMessageContext context, CancellationToken cancellation)
    {
        if (context.Envelope is SignalREnvelope se)
        {
            await se.Hub.Groups.RemoveFromGroupAsync(se.ConnectionId, GroupName, cancellation);
        }

        throw new InvalidWolverineSignalROperationException("The current message was not received from SignalR");
    }
}