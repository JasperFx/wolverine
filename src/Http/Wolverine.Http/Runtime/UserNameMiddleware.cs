using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime;

/// <summary>
/// Copies <c>HttpContext.User.Identity.Name</c> onto the message context. Called by the frame that creates
/// the context when <see cref="WolverineOptions.EnableRelayOfUserName"/> is on (GH-4741).
/// </summary>
public static class UserNameMiddleware
{
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
