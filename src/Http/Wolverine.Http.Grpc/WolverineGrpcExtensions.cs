using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProtoBuf.Grpc.Server;
using Wolverine.Runtime;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Extension methods to integrate Wolverine gRPC endpoints with ASP.NET Core.
/// </summary>
public static class WolverineGrpcExtensions
{
    /// <summary>
    /// Adds the services required for Wolverine gRPC endpoints.
    /// Supports both code-first (protobuf-net.Grpc) and proto-first (Grpc.AspNetCore) approaches.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="WolverineGrpcOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddWolverineGrpc(
        this IServiceCollection services,
        Action<WolverineGrpcOptions>? configure = null)
    {
        // AddCodeFirstGrpc() registers both protobuf-net.Grpc (code-first) and calls
        // AddGrpc() internally (required for proto-first services).
        services.AddCodeFirstGrpc();

        var options = new WolverineGrpcOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Register GrpcGraph for code generation
        services.AddSingleton<GrpcGraph>();
        services.AddSingleton<ICodeFileCollection>(sp => sp.GetRequiredService<GrpcGraph>());

        return services;
    }

    /// <summary>
    /// Discovers and maps all Wolverine gRPC service endpoint types.
    /// A type is eligible when it is decorated with <see cref="WolverineGrpcServiceAttribute"/>
    /// OR when it inherits <see cref="WolverineGrpcEndpointBase"/> and its name ends with one of
    /// the conventional suffixes: "GrpcEndpoint", "GrpcEndpoints", "GrpcService", or "GrpcServices".
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapWolverineGrpcEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var runtime = endpoints.ServiceProvider.GetRequiredService<IWolverineRuntime>();
        var grpcOptions = endpoints.ServiceProvider.GetService<WolverineGrpcOptions>();
        var logger = endpoints.ServiceProvider.GetRequiredService<ILogger<WolverineGrpcOptions>>();
        var grpcGraph = endpoints.ServiceProvider.GetRequiredService<GrpcGraph>();

        var assemblies = runtime.Options.Assemblies
            .Concat(grpcOptions?.Assemblies ?? [])
            .Distinct()
            .ToList();

        var grpcEndpointTypes = GrpcEndpointSource.FindGrpcEndpointTypes(assemblies);

        logger.LogInformation(
            "Found {Count} Wolverine gRPC endpoint type(s) in assemblies {Assemblies}",
            grpcEndpointTypes.Count,
            assemblies.Select(a => a.GetName().Name).Distinct());

        if (grpcEndpointTypes.Count == 0)
        {
            logger.LogWarning(
                "No Wolverine gRPC endpoint types were discovered. " +
                "Decorate with [WolverineGrpcService] OR inherit WolverineGrpcEndpointBase " +
                "with a suffix (GrpcEndpoint, GrpcEndpoints, GrpcService, or GrpcServices).");

            return endpoints;
        }

        // Discover services and generate code
        grpcGraph.DiscoverServices(grpcEndpointTypes);

        // Compile the generated code (happens automatically via Wolverine's IAssemblyGenerator)
        // The GrpcGraph is registered as ICodeFileCollection so Wolverine will compile it during startup

        // Map the generated handler types (or fall back to original types for backwards compatibility)
        var typesToMap = grpcEndpointTypes.Select(serviceType =>
        {
            var handlerType = grpcGraph.GetHandlerType(serviceType);
            if (handlerType != null)
            {
                logger.LogDebug("Using generated handler {HandlerType} for service {ServiceType}",
                    handlerType.Name, serviceType.Name);
                return handlerType;
            }

            // Fallback to original type for backwards compatibility
            logger.LogDebug("Using original service type {ServiceType} (no generated handler)",
                serviceType.Name);
            return serviceType;
        }).ToList();

        GrpcEndpointSource.MapGrpcEndpointTypes(endpoints, typesToMap, logger);

        return endpoints;
    }
}
