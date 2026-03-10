using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Scans assemblies for Wolverine gRPC service endpoint types.
///
/// A type qualifies when:
/// <list type="bullet">
///   <item>It is decorated with <see cref="WolverineGrpcServiceAttribute"/> — no base class required.
///         This enables both code-first (protobuf-net.Grpc) and proto-first (Grpc.AspNetCore)
///         services to be discovered automatically.</item>
///   <item>OR it inherits from <see cref="WolverineGrpcEndpointBase"/> AND its name ends with one of
///         the conventional suffixes: "GrpcEndpoint", "GrpcEndpoints", "GrpcService", "GrpcServices".</item>
/// </list>
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
    //
    // GetMethods() + LINQ is used instead of GetMethod(name) to avoid an
    // AmbiguousMatchException: in SDK.Web projects the gRPC method is present in both
    // the shared ASP.NET Core framework assembly and the Grpc.AspNetCore NuGet package.
    // The filter selects the unique overload that is:
    //   • a generic method definition
    //   • with exactly one type parameter (TService : class)
    //   • and one parameter of type IEndpointRouteBuilder
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

        // Explicit [WolverineGrpcService] attribute is sufficient on its own — the base class
        // is NOT required when the attribute is present.  This enables proto-first (Grpc.AspNetCore)
        // services that must inherit a proto-generated base class rather than
        // WolverineGrpcEndpointBase, and still want automatic discovery.
        if (type.HasAttribute<WolverineGrpcServiceAttribute>())
        {
            return true;
        }

        // Naming-convention discovery still requires WolverineGrpcEndpointBase to avoid
        // accidentally picking up unrelated classes whose names happen to end with a
        // recognised suffix (e.g. "CustomerGrpcService" that is not a gRPC service at all).
        if (!type.CanBeCastTo<WolverineGrpcEndpointBase>())
        {
            return false;
        }

        return ConventionalSuffixes.Any(suffix =>
            type.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
