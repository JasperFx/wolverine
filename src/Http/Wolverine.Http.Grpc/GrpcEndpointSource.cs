using System.Reflection;
using JasperFx.Core.Reflection;

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

        // Discovered either by explicit attribute…
        if (type.HasAttribute<WolverineGrpcServiceAttribute>())
        {
            return true;
        }

        // …or by naming convention
        return ConventionalSuffixes.Any(suffix =>
            type.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
