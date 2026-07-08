using System.Reflection;
using Asp.Versioning;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.AspVersioning;

internal static class AdvertisedVersionResolver
{
    /// <summary>
    /// Resolves all API versions advertised by a handler method.
    /// </summary>
    /// <param name="method">The handler method.</param>
    /// <returns>
    /// An ordered, distinct list of <see cref="ApiVersionResolution"/>; empty when no advertise
    /// version attributes are present.
    /// </returns>
    public static IReadOnlyList<ApiVersionResolution> ResolveAdvertised(MethodInfo method)
    {
        var mergedAttributes = getMergedAdvertiseAttributes(method)
            .Cast<IApiVersionProvider>()
            .ToList();
        var versions = mergedAttributes.SelectMany(attr => attr.Versions).Distinct().ToList();

        return ApiVersionResolver.BuildResolutions(versions, mergedAttributes);
    }

    private static IEnumerable<AdvertiseApiVersionsAttribute> getMergedAdvertiseAttributes(
        MethodInfo method
    )
    {
        // Unlike [ApiVersion] and [MapToApiVersion], [AdvertiseApiVersions] is additive across method
        // and class. We can just merge them without any special logic.
        return method
            .GetCustomAttributes<AdvertiseApiVersionsAttribute>(inherit: false)
            .Concat(
                method.DeclaringType?.GetCustomAttributes<AdvertiseApiVersionsAttribute>(
                    inherit: false
                ) ?? []
            );
    }
}
