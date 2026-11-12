using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime;

/// <summary>
/// Copies the authenticated user onto the message context. Called by the frame that creates the context when
/// <see cref="WolverineOptions.EnableRelayOfUserName"/> is on (GH-4741), which it is by default from Wolverine 7.0.
/// </summary>
public static class UserNameMiddleware
{
    /// <summary>
    /// Relay <c>HttpContext.User.Identity.Name</c>
    /// </summary>
    public static void Apply(HttpContext httpContext, IMessageContext messaging)
    {
        relay(httpContext.User?.Identity?.Name, messaging);
    }

    /// <summary>
    /// GH-5520. Relay the value of the first claim of <paramref name="claimType"/> instead of <c>Identity.Name</c>,
    /// as configured by <see cref="WolverineHttpOptions.UserNameClaimType"/>
    /// </summary>
    public static void Apply(HttpContext httpContext, IMessageContext messaging, string claimType)
    {
        relay(httpContext.User?.FindFirst(claimType)?.Value, messaging);
    }

    private static void relay(string? userName, IMessageContext messaging)
    {
        if (userName is not null)
        {
            messaging.UserName = userName;
            Activity.Current?.SetTag("enduser.id", userName);
        }
    }
}
