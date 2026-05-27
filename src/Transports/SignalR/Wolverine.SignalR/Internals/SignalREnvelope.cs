using System.Security.Claims;
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
        Principal = context.User;
    }

    public new string? UserName { get; set; }

    public string ConnectionId { get; set; }

    /// <summary>
    ///     The connecting client's <see cref="ClaimsPrincipal" /> (<c>HubCallerContext.User</c>), captured
    ///     so Wolverine handlers/middleware can perform per-message authorization from the principal's
    ///     claims — not just connection-time <c>[Authorize]</c> on the hub. Null if the connection is
    ///     unauthenticated. See GH-2927.
    /// </summary>
    public ClaimsPrincipal? Principal { get; }
}