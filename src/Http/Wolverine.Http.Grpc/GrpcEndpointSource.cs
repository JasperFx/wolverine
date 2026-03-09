using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Scans assemblies for Wolverine gRPC service endpoint types – concrete classes
/// that are decorated with <see cref="WolverineGrpcServiceAttribute"/> OR that
/// inherit from <see cref="WolverineGrpcEndpointBase"/> and whose name ends with
/// "GrpcEndpoint", "GrpcEndpoints", "GrpcService", or "GrpcServices".
/// </summary>
internal static class GrpcEndpointSource
{
    private static readonly string[] ConventionalSuffixes =
    [
        "GrpcEndpoint",
        "GrpcEndpoints",
        "GrpcService",
        "GrpcServices"
    ];

    // Cached MethodInfo for GrpcEndpointRouteBuilderExtensions.MapGrpcService<T>().
    // Unlike MapWolverineEndpoints (which uses an EndpointDataSource / data-source pattern),
    // gRPC service registration requires calling the generic MapGrpcService<T>() API from
    // Grpc.AspNetCore. Because the concrete service types are discovered at runtime via
    // assembly scanning, a one-time MakeGenericMethod call per type is the correct approach.
    // The MethodInfo is cached as a static field so the reflection lookup itself only happens
    // once per application lifetime, not once per registered service type.
    private static readonly MethodInfo MapGrpcServiceMethod =
        typeof(GrpcEndpointRouteBuilderExtensions)
            .GetMethod(nameof(GrpcEndpointRouteBuilderExtensions.MapGrpcService))!;

    /// <summary>
    /// Returns all concrete, non-generic, public types in the supplied assemblies
    /// that qualify as Wolverine gRPC service endpoints.
    /// </summary>
    internal static IReadOnlyList<Type> FindGrpcEndpointTypes(IEnumerable<Assembly> assemblies)
    {
        return assemblies
            .SelectMany(a => a.ExportedTypes)
            .Where(IsGrpcEndpointType)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Maps each discovered gRPC endpoint type into the ASP.NET Core routing pipeline
    /// by calling <c>MapGrpcService&lt;T&gt;()</c> for every type in <paramref name="grpcEndpointTypes"/>.
    /// Optionally logs a debug message for each type as it is registered.
    /// </summary>
    /// <remarks>
    /// The underlying <c>Grpc.AspNetCore</c> library only exposes a generic
    /// <c>MapGrpcService&lt;T&gt;</c> API; there is no non-generic equivalent. Because the
    /// endpoint types are discovered at runtime, <see cref="MethodInfo.MakeGenericMethod"/>
    /// is used here — the MethodInfo itself is cached once as a static field. This is the
    /// correct approach given the gRPC library constraints, and differs intentionally from
    /// <c>MapWolverineEndpoints</c>, which uses an <c>EndpointDataSource</c> / data-source
    /// pattern that does not require runtime generic invocation.
    /// </remarks>
    internal static void MapGrpcEndpointTypes(
        IEndpointRouteBuilder endpoints,
        IReadOnlyList<Type> grpcEndpointTypes,
        ILogger? logger = null)
    {
        foreach (var serviceType in grpcEndpointTypes)
        {
            logger?.LogDebug("Mapping Wolverine gRPC endpoint: {Type}", serviceType.FullName);
            MapGrpcServiceMethod.MakeGenericMethod(serviceType).Invoke(null, [endpoints]);
        }
    }

    internal static bool IsGrpcEndpointType(Type type)
    {
        if (!type.IsPublic || type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
        {
            return false;
        }

        // Must inherit from WolverineGrpcEndpointBase
        if (!type.CanBeCastTo<WolverineGrpcEndpointBase>())
        {
            return false;
        }

        // Discovered either by explicit attribute...
        if (type.HasAttribute<WolverineGrpcServiceAttribute>())
        {
            return true;
        }

        // ...or by naming convention
        return ConventionalSuffixes.Any(suffix =>
            type.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
