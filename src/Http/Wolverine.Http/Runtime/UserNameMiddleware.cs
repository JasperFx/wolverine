using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime;

public static class UserNameMiddleware
{
    /// <summary>
    /// Copy the authenticated user name onto the message context. Called from generated code by
    /// <c>CreateMessageContextWithMaybeTenantFrame</c> when
    /// <see cref="WolverineOptions.EnableRelayOfUserName" /> is true -- see GH-4741 for why this is
    /// emitted from the frame that creates the MessageContext rather than from an HTTP policy that
    /// inserts a middleware frame.
    /// </summary>
    public static void Apply(HttpContext httpContext, IMessageContext messaging)
    {
        var userName = httpContext.User?.Identity?.Name;
        if (userName is not null)
        {
            messaging.UserName = userName;
            Activity.Current?.SetTag("enduser.id", userName);
        }
    }
}
