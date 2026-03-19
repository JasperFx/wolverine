using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Scans assemblies for Wolverine gRPC service endpoint types.
/// A type qualifies when it is decorated with <see cref="WolverineGrpcServiceAttribute"/> (no base class required),
/// OR when it inherits <see cref="WolverineGrpcEndpointBase"/> and its name ends with one of the conventional
/// suffixes: "GrpcEndpoint", "GrpcEndpoints", "GrpcService", or "GrpcServices".
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

    // Cached MethodInfo for MapGrpcService<T>().
    // gRPC service registration requires calling the generic MapGrpcService<T>() API;
    // the MethodInfo is cached to avoid repeated reflection lookups.
    // GetMethods() + LINQ is used instead of GetMethod(name) to avoid AmbiguousMatchException
    // in SDK.Web projects where the method exists in both ASP.NET Core and Grpc.AspNetCore assemblies.
    private static readonly MethodInfo MapGrpcServiceMethod =
        typeof(GrpcEndpointRouteBuilderExtensions)
            .GetMethods()
            .Single(m => m.Name == nameof(GrpcEndpointRouteBuilderExtensions.MapGrpcService)
                         && m.IsGenericMethodDefinition
                         && m.GetGenericArguments().Length == 1
                         && m.GetParameters().Length == 1
                         && m.GetParameters()[0].ParameterType == typeof(IEndpointRouteBuilder));

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
    /// Maps each discovered gRPC endpoint type by calling <c>MapGrpcService&lt;T&gt;()</c>.
    /// </summary>
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
        // Abstract types are only allowed if they have [WolverineGrpcService] AND do NOT inherit WolverineGrpcEndpointBase
        // (for code generation pattern with proto-first services)
        bool isEligibleAbstract = type.IsAbstract
            && type.HasAttribute<WolverineGrpcServiceAttribute>()
            && !type.CanBeCastTo<WolverineGrpcEndpointBase>();

        if (!type.IsPublic || (type.IsAbstract && !isEligibleAbstract) || type.IsInterface || type.IsGenericTypeDefinition)
        {
            return false;
        }

        // [WolverineGrpcService] attribute is sufficient alone (enables proto-first and abstract services).
        if (type.HasAttribute<WolverineGrpcServiceAttribute>())
        {
            return true;
        }

        // Naming-convention discovery requires WolverineGrpcEndpointBase to avoid false positives.
        if (!type.CanBeCastTo<WolverineGrpcEndpointBase>())
        {
            return false;
        }

        return ConventionalSuffixes.Any(suffix =>
            type.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
