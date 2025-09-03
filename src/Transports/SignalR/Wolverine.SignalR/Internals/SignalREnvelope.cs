using Microsoft.AspNetCore.SignalR;

namespace Wolverine.SignalR.Internals;

public class SignalREnvelope : Envelope
{
    public Hub Hub { get; }

    public SignalREnvelope()
    {
    }

    public SignalREnvelope(HubCallerContext context, Hub hub)
    {
        Hub = hub;
        ConnectionId = context.ConnectionId;
        UserName = context.UserIdentifier;
    }

    public string? UserName { get; set; }

    public string ConnectionId { get; set; }
}