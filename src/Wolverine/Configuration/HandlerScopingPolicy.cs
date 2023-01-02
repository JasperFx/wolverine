using System.Linq;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

internal class HandlerScopingPolicy : IRegistrationPolicy
{
    private readonly HandlerGraph _handlers;

    public HandlerScopingPolicy(HandlerGraph handlers)
    {
        _handlers = handlers;
    }

    public void Apply(ServiceRegistry services)
    {
        var handlerTypes = _handlers.Chains.SelectMany(x => x.Handlers)
            .Select(x => x.HandlerType).Where(x => !x.IsStatic());

        foreach (var handlerType in handlerTypes)
        {
            services.AddScoped(handlerType);
        }
    }

}