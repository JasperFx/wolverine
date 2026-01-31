using Microsoft.AspNetCore.SignalR;

namespace Wolverine.SignalR.Internals;

public class SignalREnvelope : Envelope
{
    public IHubContext<Hub> HubContext { get; }

    public SignalREnvelope(HubCallerContext context, IHubContext<Hub> hubContext)
    {
        HubContext = hubContext;
        ConnectionId = context.ConnectionId;
        UserName = context.UserIdentifier;
    }

    public string? UserName { get; set; }

    public string ConnectionId { get; set; }
}