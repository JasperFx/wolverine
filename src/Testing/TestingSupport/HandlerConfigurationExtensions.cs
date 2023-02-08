using System;
using Wolverine;
using Wolverine.Configuration;

namespace TestingSupport;

public static class HandlerConfigurationExtensions
{
    public static WolverineOptions DisableConventionalDiscovery(this WolverineOptions handlers)
    {
        handlers.Policies.Discovery(x => x.DisableConventionalDiscovery());

        return handlers;
    }

    public static WolverineOptions OnlyType<T>(this WolverineOptions handlers)
    {
        handlers.Policies.Discovery(x =>
        {
            x.DisableConventionalDiscovery();
            x.IncludeType<T>();
        });

        return handlers;
    }

    public static WolverineOptions IncludeType<T>(this WolverineOptions handlers)
    {
        handlers.Policies.Discovery(x => x.IncludeType<T>());

        return handlers;
    }

    public static WolverineOptions IncludeType(this WolverineOptions handlers, Type handlerType)
    {
        handlers.Policies.Discovery(x => x.IncludeType(handlerType));

        return handlers;
    }
}