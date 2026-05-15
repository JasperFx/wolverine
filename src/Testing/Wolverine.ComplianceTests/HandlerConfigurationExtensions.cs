using Wolverine;

namespace Wolverine.ComplianceTests;

public static class HandlerConfigurationExtensions
{
    /// <summary>
    /// Using this option disables all assembly scanning to discover handler candidates!!!
    /// You will have to manually register handler types if you choose this option
    /// </summary>
    /// <param name="handlers"></param>
    /// <returns></returns>
    public static WolverineOptions DisableConventionalDiscovery(this WolverineOptions handlers)
    {
        handlers.Discovery.DisableConventionalDiscovery();

        return handlers;
    }

    /// <summary>
    /// Explicitly add this type to Wolverine as a handler type
    /// </summary>
    /// <param name="handlers"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static WolverineOptions IncludeType<T>(this WolverineOptions handlers)
    {
        handlers.Discovery.IncludeType<T>();

        return handlers;
    }

    /// <summary>
    /// Explicitly add this type to Wolverine as a handler type
    /// </summary>
    /// <param name="handlers"></param>
    /// <param name="handlerType"></param>
    /// <returns></returns>
    public static WolverineOptions IncludeType(this WolverineOptions handlers, Type handlerType)
    {
        handlers.Discovery.IncludeType(handlerType);

        return handlers;
    }
}