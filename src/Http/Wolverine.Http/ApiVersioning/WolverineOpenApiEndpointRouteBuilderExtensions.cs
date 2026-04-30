using Asp.Versioning;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Extensions for surfacing Wolverine API-versioning information into ASP.NET Core endpoint
/// routing — primarily for wiring SwaggerUI / Scalar version dropdowns.
/// </summary>
public static class WolverineOpenApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Returns one description per discovered API version, sorted ascending by major then minor.
    /// Returns an empty list when API versioning is not configured or no versioned endpoint
    /// has been discovered.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (typically the <c>WebApplication</c> instance).</param>
    /// <returns>
    /// A read-only list of <see cref="WolverineApiVersionDescription"/> instances, one per
    /// distinct <see cref="ApiVersion"/> found across all registered Wolverine HTTP chains,
    /// sorted ascending by major version then minor version.
    /// </returns>
    public static IReadOnlyList<WolverineApiVersionDescription> DescribeWolverineApiVersions(
        this IEndpointRouteBuilder endpoints)
    {
        var httpOptions = endpoints.ServiceProvider.GetService<WolverineHttpOptions>();

        // Not configured or AddWolverineHttp() was never called — return empty.
        if (httpOptions?.ApiVersioning is null)
            return Array.Empty<WolverineApiVersionDescription>();

        var graph = httpOptions.Endpoints;

        // MapWolverineEndpoints has not been called yet — return empty rather than throwing.
        if (graph is null)
            return Array.Empty<WolverineApiVersionDescription>();

        var apiVersioning = httpOptions.ApiVersioning;
        var openApiOptions = apiVersioning.OpenApi;

        // Gather every chain that has a resolved ApiVersion, then group by version.
        var byVersion = graph.Chains
            .Where(c => c.ApiVersion is not null)
            .GroupBy(c => c.ApiVersion!)
            .ToList();

        if (byVersion.Count == 0)
            return Array.Empty<WolverineApiVersionDescription>();

        var descriptions = new List<WolverineApiVersionDescription>(byVersion.Count);

        foreach (var group in byVersion)
        {
            var version = group.Key;

            var documentName = openApiOptions.DocumentNameStrategy(version);
            var displayName = $"API {documentName}";

            // A version is deprecated when any chain in the group has a deprecation policy,
            // OR when the options-level DeprecationPolicies map contains an entry for it.
            var isDeprecated = group.Any(c => c.DeprecationPolicy is not null)
                || apiVersioning.DeprecationPolicies.ContainsKey(version);

            // Prefer the policy already attached to a chain (set during policy application);
            // fall back to the options-level map (handles the case where the chain was
            // PassThrough / not reachable by the policy).
            var sunsetPolicy = group.Select(c => c.SunsetPolicy).FirstOrDefault(p => p is not null)
                ?? apiVersioning.SunsetPolicies.GetValueOrDefault(version);

            descriptions.Add(new WolverineApiVersionDescription(
                version,
                documentName,
                displayName,
                isDeprecated,
                sunsetPolicy));
        }

        // Sort ascending by major version then minor version.
        // Treat null MajorVersion (date-based versions) as int.MaxValue so they sort last.
        descriptions.Sort((a, b) =>
        {
            var majorA = a.ApiVersion.MajorVersion ?? int.MaxValue;
            var majorB = b.ApiVersion.MajorVersion ?? int.MaxValue;

            var majorCmp = majorA.CompareTo(majorB);
            if (majorCmp != 0)
                return majorCmp;

            var minorA = a.ApiVersion.MinorVersion ?? 0;
            var minorB = b.ApiVersion.MinorVersion ?? 0;
            return minorA.CompareTo(minorB);
        });

        return descriptions.AsReadOnly();
    }
}
