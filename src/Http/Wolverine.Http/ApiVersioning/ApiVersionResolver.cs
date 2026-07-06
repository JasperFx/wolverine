using System.Reflection;
using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

internal readonly record struct ApiVersionResolution(ApiVersion Version, bool IsDeprecated);

internal static class ApiVersionResolver
{
    /// <summary>
    /// Resolves all API versions a handler method serves. Resolution rules:
    /// <list type="bullet">
    ///   <item>Method-level <c>[ApiVersion]</c> attributes (if any) override class-level entirely.</item>
    ///   <item>Method-level <c>[MapToApiVersion]</c> filters class-level versions to the listed subset.</item>
    ///   <item>A method may not carry both <c>[ApiVersion]</c> and <c>[MapToApiVersion]</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="method">The handler method.</param>
    /// <returns>An ordered, distinct list of <see cref="ApiVersionResolution"/>; empty when no version attributes are present.</returns>
    /// <exception cref="InvalidOperationException">Thrown when both <c>[ApiVersion]</c> and <c>[MapToApiVersion]</c> are declared on the same method, or when <c>[MapToApiVersion]</c> lists a version not declared on the class.</exception>
    public static IReadOnlyList<ApiVersionResolution> ResolveVersions(MethodInfo method)
    {
        var methodApiVersionAttrs = method.GetCustomAttributes<ApiVersionAttribute>(inherit: false).ToList();
        var methodMapToAttrs = method.GetCustomAttributes<MapToApiVersionAttribute>(inherit: false).ToList();

        if (methodApiVersionAttrs.Count > 0 && methodMapToAttrs.Count > 0)
        {
            throw new InvalidOperationException(
                $"Handler method '{MethodIdentity(method)}' declares both [ApiVersion] and [MapToApiVersion] attributes. " +
                "Use only one: [ApiVersion] sets versions independently of the class; " +
                "[MapToApiVersion] selects a subset of the class-level versions.");
        }

        // Method-level [ApiVersion] overrides class entirely.
        if (methodApiVersionAttrs.Count > 0)
        {
            var methodVersions = methodApiVersionAttrs.SelectMany(a => a.Versions).Distinct().ToList();
            return BuildResolutions(methodVersions, methodApiVersionAttrs);
        }

        var classApiVersionAttrs = method.DeclaringType?
            .GetCustomAttributes<ApiVersionAttribute>(inherit: false)
            .ToList() ?? new List<ApiVersionAttribute>();

        // Method-level [MapToApiVersion] filters class-level versions.
        if (methodMapToAttrs.Count > 0)
        {
            if (classApiVersionAttrs.Count == 0)
            {
                var className = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "?";
                throw new InvalidOperationException(
                    $"Handler method '{MethodIdentity(method)}' has [MapToApiVersion] but the declaring class '{className}' has no [ApiVersion] attribute. " +
                    "[MapToApiVersion] only filters class-level versions; declare class-level [ApiVersion] first.");
            }

            var classVersions = classApiVersionAttrs.SelectMany(a => a.Versions).Distinct().ToList();
            var requestedVersions = methodMapToAttrs.SelectMany(a => a.Versions).Distinct().ToList();

            var missing = requestedVersions.Where(v => !classVersions.Contains(v)).ToList();
            if (missing.Count > 0)
            {
                var className = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "?";
                var missingList = string.Join(", ", missing.Select(v => v.ToString()));
                var classList = string.Join(", ", classVersions.Select(v => v.ToString()));
                throw new InvalidOperationException(
                    $"Handler method '{MethodIdentity(method)}' has [MapToApiVersion({missingList})] but the declaring class '{className}' " +
                    $"only declares [ApiVersion] for [{classList}]. [MapToApiVersion] must list a subset of class-level versions.");
            }

            // Deprecation flags on the class still apply to the filtered subset.
            return BuildResolutions(requestedVersions, classApiVersionAttrs);
        }

        if (classApiVersionAttrs.Count == 0) return Array.Empty<ApiVersionResolution>();
        var classDeclared = classApiVersionAttrs.SelectMany(a => a.Versions).Distinct().ToList();
        return BuildResolutions(classDeclared, classApiVersionAttrs);
    }

    /// <summary>
    /// Builds resolutions for the given <paramref name="versions"/> in order, marking each as
    /// deprecated when any provider in <paramref name="versionProviders"/> declares the version
    /// with the <see cref="ApiVersionProviderOptions.Deprecated"/> flag.
    /// </summary>
    /// <param name="versions">The API versions to build resolutions for.</param>
    /// <param name="versionProviders">
    /// The API version providers whose <see cref="ApiVersionProviderOptions.Deprecated"/> flag drives
    /// per-version deprecation (e.g. <c>[ApiVersion]</c>, <c>[MapToApiVersion]</c>,
    /// <c>[AdvertiseApiVersions]</c>).
    /// </param>
    /// <returns>A read-only list of API version resolutions.</returns>
    public static IReadOnlyList<ApiVersionResolution> BuildResolutions(
        IReadOnlyCollection<ApiVersion> versions,
        IReadOnlyCollection<IApiVersionProvider> versionProviders
    )
    {
        var result = new List<ApiVersionResolution>(versions.Count);
        foreach (var version in versions)
        {
            var isDeprecated = versionProviders.Any(a =>
                a.Options.HasFlag(ApiVersionProviderOptions.Deprecated)
                && a.Versions.Contains(version)
            );

            result.Add(new ApiVersionResolution(version, isDeprecated));
        }
        return result;
    }

    private static string MethodIdentity(MethodInfo method) =>
        (method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "?") + "." + method.Name;
}
