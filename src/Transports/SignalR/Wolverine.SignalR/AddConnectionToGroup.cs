using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

/// <summary>
/// Side Effect that will add the SignalR connection that received the original
/// message to a named SignalR group on the originating Hub
/// </summary>
/// <param name="GroupName"></param>
public record AddConnectionToGroup(string GroupName) : ISignalRAction
{
    public async ValueTask ExecuteAsync(IMessageContext context, CancellationToken cancellation)
    {
        if (context.Envelope is SignalREnvelope se)
        {
            await se.Hub.Groups.AddToGroupAsync(se.ConnectionId, GroupName, cancellation);
        }

        throw new InvalidWolverineSignalROperationException("The current message was not received from SignalR");
    }
}