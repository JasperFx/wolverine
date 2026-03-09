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
    /// Call this in your <c>IServiceCollection</c> configuration, typically alongside
    /// <see cref="WolverineHttpEndpointRouteBuilderExtensions.AddWolverineHttp"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="WolverineGrpcOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddWolverineHttp();
    /// builder.Services.AddWolverineGrpc();
    /// </code>
    /// </example>
    public static IServiceCollection AddWolverineGrpc(
        this IServiceCollection services,
        Action<WolverineGrpcOptions>? configure = null)
    {
        // Register code-first gRPC support (protobuf-net.Grpc)
        services.AddCodeFirstGrpc();

        var options = new WolverineGrpcOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        return services;
    }

    /// <summary>
    /// Discovers and maps all Wolverine gRPC service endpoint types as gRPC services in
    /// the ASP.NET Core routing pipeline. Endpoints are found by scanning the same
    /// assemblies used by Wolverine for handler/HTTP endpoint discovery, plus any
    /// additional assemblies configured in <see cref="WolverineGrpcOptions"/>.
    /// </summary>
    /// <remarks>
    /// A type is eligible when it:
    /// <list type="bullet">
    ///   <item>Inherits from <see cref="WolverineGrpcEndpointBase"/></item>
    ///   <item>Is decorated with <see cref="WolverineGrpcServiceAttribute"/> OR its name ends with
    ///         "GrpcEndpoint", "GrpcEndpoints", "GrpcService", or "GrpcServices"</item>
    /// </list>
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapWolverineGrpcEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var runtime = (IWolverineRuntime)endpoints.ServiceProvider.GetRequiredService<IWolverineRuntime>();
        var grpcOptions = endpoints.ServiceProvider.GetService<WolverineGrpcOptions>();
        var logger = endpoints.ServiceProvider.GetRequiredService<ILogger<WolverineGrpcOptions>>();

        var assemblies = runtime.Options.Assemblies.ToList();
        if (grpcOptions != null)
        {
            assemblies.AddRange(grpcOptions.Assemblies);
        }

        var grpcEndpointTypes = GrpcEndpointSource.FindGrpcEndpointTypes(assemblies);

        logger.LogInformation(
            "Found {Count} Wolverine gRPC endpoint type(s) in assemblies {Assemblies}",
            grpcEndpointTypes.Count,
            assemblies.Select(a => a.GetName().Name).Distinct());

        if (grpcEndpointTypes.Count == 0)
        {
            logger.LogWarning(
                "No Wolverine gRPC endpoint types were discovered. Ensure your endpoint classes " +
                "inherit from WolverineGrpcEndpointBase and either have a [WolverineGrpcService] " +
                "attribute or a name ending in 'GrpcEndpoint', 'GrpcService', etc.");
        }

        // Use reflection to call the generic MapGrpcService<T>() for each discovered type.
        var mapMethod = typeof(GrpcEndpointRouteBuilderExtensions)
            .GetMethod(nameof(GrpcEndpointRouteBuilderExtensions.MapGrpcService))!;

        foreach (var serviceType in grpcEndpointTypes)
        {
            logger.LogDebug("Mapping Wolverine gRPC endpoint: {Type}", serviceType.FullName);
            mapMethod.MakeGenericMethod(serviceType).Invoke(null, [endpoints]);
        }

        return endpoints;
    }
}
