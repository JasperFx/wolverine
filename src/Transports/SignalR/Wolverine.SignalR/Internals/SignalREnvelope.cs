using Microsoft.AspNetCore.SignalR;

namespace Wolverine.SignalR.Internals;

public class SignalREnvelope : Envelope
{
    public IHubContext<WolverineHub> HubContext { get; }

    public SignalREnvelope(HubCallerContext context, IHubContext<WolverineHub> hubContext)
    {
        HubContext = hubContext;
        ConnectionId = context.ConnectionId;
        UserName = context.UserIdentifier;
    }

    public string? UserName { get; set; }

    public string ConnectionId { get; set; }
}