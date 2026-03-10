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
    /// <remarks>
    /// Supports both the <strong>code-first</strong> approach (protobuf-net.Grpc, using
    /// <see cref="WolverineGrpcEndpointBase"/>) and the <strong>proto-first</strong> approach
    /// (Grpc.AspNetCore + <c>.proto</c> files, using <see cref="WolverineGrpcServiceAttribute"/>
    /// with constructor-injected <c>IMessageBus</c>).
    /// </remarks>
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
        // AddCodeFirstGrpc() registers the protobuf-net.Grpc code-first pipeline, which also
        // calls the underlying services.AddGrpc() required by Grpc.AspNetCore for proto-first services.
        // Both code-first and proto-first services are therefore supported after this call.
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
    /// <para>
    /// A type is eligible when it satisfies <strong>either</strong> of the following:
    /// <list type="bullet">
    ///   <item>It is decorated with <see cref="WolverineGrpcServiceAttribute"/>
    ///         (<see cref="WolverineGrpcEndpointBase"/> is <strong>not</strong> required —
    ///         this enables proto-first services that inherit a proto-generated base class)</item>
    ///   <item>OR it inherits from <see cref="WolverineGrpcEndpointBase"/> AND its name ends with
    ///         "GrpcEndpoint", "GrpcEndpoints", "GrpcService", or "GrpcServices"</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Relationship to <c>MapWolverineEndpoints</c>:</strong>
    /// <c>MapWolverineEndpoints</c> (from <c>Wolverine.Http</c>) is dedicated to HTTP endpoints
    /// and registers routes via an <c>EndpointDataSource</c> data-source pattern — no runtime
    /// reflection is involved in calling the route builder API.
    /// This method, by contrast, must call the generic <c>MapGrpcService&lt;T&gt;()</c> API from
    /// <c>protobuf-net.Grpc.AspNetCore</c> for each discovered type. Because that API only exposes
    /// a generic overload and the concrete service types are not known until assembly scanning runs
    /// at startup, <see cref="System.Reflection.MethodInfo.MakeGenericMethod"/> is used
    /// (with the <c>MethodInfo</c> cached statically in <see cref="GrpcEndpointSource"/>).
    /// This is the correct and intentional approach given the constraints of the underlying gRPC
    /// library, and mirrors how the Wolverine.Http package would behave if <c>MapGrpcService</c>
    /// had a non-generic overload.
    /// </para>
    /// <para>
    /// <c>MapWolverineEndpoints</c> is exclusive to <c>Wolverine.Http</c> (HTTP/REST endpoints)
    /// and is not extended here. gRPC endpoint registration is intentionally kept in this separate
    /// method to maintain a clear separation of concerns between HTTP and gRPC transports.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapWolverineGrpcEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var runtime = endpoints.ServiceProvider.GetRequiredService<IWolverineRuntime>();
        var grpcOptions = endpoints.ServiceProvider.GetService<WolverineGrpcOptions>();
        var logger = endpoints.ServiceProvider.GetRequiredService<ILogger<WolverineGrpcOptions>>();

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
                "A type is eligible when it is decorated with [WolverineGrpcService] " +
                "(sufficient on its own — no base class required, enabling proto-first services) " +
                "OR when it inherits WolverineGrpcEndpointBase and its name ends with " +
                "'GrpcEndpoint', 'GrpcEndpoints', 'GrpcService', or 'GrpcServices'.");

            return endpoints;
        }

        GrpcEndpointSource.MapGrpcEndpointTypes(endpoints, grpcEndpointTypes, logger);

        return endpoints;
    }
}
