using System;
using Wolverine;
using Wolverine.Configuration;

namespace TestingSupport;

public static class HandlerConfigurationExtensions
{
    public static WolverineOptions DisableConventionalDiscovery(this WolverineOptions handlers)
    {
        handlers.Discovery.DisableConventionalDiscovery();

        return handlers;
    }

    public static WolverineOptions OnlyType<T>(this WolverineOptions handlers)
    {
        handlers.Discovery.DisableConventionalDiscovery().IncludeType<T>();

        return handlers;
    }

    public static WolverineOptions IncludeType<T>(this WolverineOptions handlers)
    {
        handlers.Discovery.IncludeType<T>();

        return handlers;
    }

    public static WolverineOptions IncludeType(this WolverineOptions handlers, Type handlerType)
    {
        handlers.Discovery.IncludeType(handlerType);

        return handlers;
    }
}