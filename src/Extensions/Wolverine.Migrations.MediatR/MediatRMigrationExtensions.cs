using System.Reflection;
using JasperFx.Core.Reflection;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime.Handlers;
using Wolverine.Shims.MediatR;

namespace Wolverine;

/// <summary>
/// Extension methods for migrating MediatR handlers to Wolverine
/// </summary>
public static class MediatRMigrationExtensions
{
    /// <summary>
    /// Scans assemblies for MediatR IRequestHandler implementations and registers them as Wolverine handlers.
    /// This allows existing MediatR handlers to work within Wolverine without modification.
    /// </summary>
    /// <param name="options">The Wolverine options</param>
    /// <param name="assemblies">Assemblies to scan for MediatR handlers. If not specified, uses the application assembly.</param>
    /// <returns>The Wolverine options for chaining</returns>
    public static WolverineOptions MigrateFromMediatR(this WolverineOptions options, params Assembly[] assemblies)
    {
        var assembliesToScan = assemblies.Length > 0 ? assemblies : [options.ApplicationAssembly!];

        foreach (var assembly in assembliesToScan)
        {
            if (assembly == null) continue;

            // Find all MediatR handler types
            var handlerTypes = assembly.ExportedTypes
                .Where(t => t.IsConcrete() && t.IsPublic && !t.IsAbstract)
                .Where(t =>
                    t.GetInterfaces().Any(i => i.IsGenericType &&
                        (i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>) ||
                         i.GetGenericTypeDefinition() == typeof(IRequestHandler<>))))
                .ToArray();

            foreach (var handlerType in handlerTypes)
            {
                // Find which request types this handler handles
                foreach (var interfaceType in handlerType.GetInterfaces()
                             .Where(i => i.IsGenericType))
                {
                    var genericDef = interfaceType.GetGenericTypeDefinition();

                    if (genericDef == typeof(IRequestHandler<,>))
                    {
                        var requestType = interfaceType.GetGenericArguments()[0];
                        var responseType = interfaceType.GetGenericArguments()[1];

                        RegisterMediatRHandler(options, handlerType, requestType, responseType);
                    }
                    else if (genericDef == typeof(IRequestHandler<>))
                    {
                        var requestType = interfaceType.GetGenericArguments()[0];

                        RegisterMediatRHandler(options, handlerType, requestType, null);
                    }
                }
            }
        }

        return options;
    }

    private static void RegisterMediatRHandler(WolverineOptions options, Type handlerType, Type requestType, Type? responseType)
    {
        // Register the MediatR handler interface in the container so it can be resolved at runtime
        // This ensures the shim can resolve the handler via IRequestHandler<TRequest, TResponse>
        if (responseType != null)
        {
            var handlerInterfaceType = typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType);
            options.Services.AddTransient(handlerInterfaceType, handlerType);
        }
        else
        {
            var handlerInterfaceType = typeof(IRequestHandler<>).MakeGenericType(requestType);
            options.Services.AddTransient(handlerInterfaceType, handlerType);
        }

        // Create and register the shim handler instance
        // The shim itself has no dependencies, but will resolve the MediatR handler from DI at runtime
        if (responseType != null)
        {
            var shimType = typeof(MediatRHandlerShim<,>).MakeGenericType(requestType, responseType);
            var shim = (IMessageHandler)Activator.CreateInstance(shimType)!;
            options.AddMessageHandler(requestType, shim);
        }
        else
        {
            var shimType = typeof(MediatRHandlerShim<>).MakeGenericType(requestType);
            var shim = (IMessageHandler)Activator.CreateInstance(shimType)!;
            options.AddMessageHandler(requestType, shim);
        }
    }
}
