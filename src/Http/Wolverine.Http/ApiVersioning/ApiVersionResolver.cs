using System.Reflection;
using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

internal readonly record struct ApiVersionResolution(ApiVersion Version, bool IsDeprecated);

internal static class ApiVersionResolver
{
    /// <summary>
    /// Resolves the API version declared on a handler method. The method's [ApiVersion] wins;
    /// the declaring class's [ApiVersion] is used as a fallback.
    /// </summary>
    /// <param name="method">The handler method.</param>
    /// <returns>The single resolved ApiVersion with deprecation status, or null if no [ApiVersion] is present.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the method (or, when no method-level attribute is present, the declaring class) declares more than one ApiVersion in a single ApiVersionAttribute or via multiple attributes.</exception>
    public static ApiVersionResolution? Resolve(MethodInfo method)
    {
        var methodAttrs = method.GetCustomAttributes<ApiVersionAttribute>(inherit: false).ToList();
        List<ApiVersionAttribute> winningAttrs;

        if (methodAttrs.Count > 0)
        {
            winningAttrs = methodAttrs;
        }
        else
        {
            var classAttrs = method.DeclaringType?.GetCustomAttributes<ApiVersionAttribute>(inherit: false).ToList();
            if (classAttrs is null || classAttrs.Count == 0)
            {
                return null;
            }

            winningAttrs = classAttrs;
        }

        var versions = winningAttrs.SelectMany(a => a.Versions).Distinct().ToList();

        if (versions.Count == 1)
        {
            var version = versions[0];
            var isDeprecated = winningAttrs.Any(a => a.Deprecated && a.Versions.Contains(version));
            return new ApiVersionResolution(version, isDeprecated);
        }

        var methodIdentity = (method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "?") + "." + method.Name;
        var versionList = string.Join(", ", versions);
        throw new InvalidOperationException(
            $"Handler method '{methodIdentity}' declares multiple API versions [{versionList}]. " +
            "Multi-version handlers are not supported in this version of Wolverine.Http API versioning.");
    }
}
