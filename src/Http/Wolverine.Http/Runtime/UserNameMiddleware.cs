using System.Diagnostics;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime;

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

internal class UserNamePolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var options = container.GetInstance<WolverineOptions>();
        if (!options.EnableRelayOfUserName) return;

        foreach (var chain in chains)
        {
            var serviceDependencies = chain.ServiceDependencies(container, Type.EmptyTypes).ToArray();
            if (serviceDependencies.Contains(typeof(IMessageContext)) ||
                serviceDependencies.Contains(typeof(IMessageBus)))
            {
                chain.Middleware.Insert(0,
                    new MethodCall(typeof(UserNameMiddleware), nameof(UserNameMiddleware.Apply)));
            }
        }
    }
}
