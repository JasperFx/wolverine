using Asp.Versioning;
using System.Globalization;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// OpenAPI integration options exposed via <see cref="WolverineApiVersioningOptions.OpenApi"/>.
/// </summary>
public sealed class WolverineApiVersioningOpenApiOptions
{
    /// <summary>
    /// Strategy that maps an <see cref="ApiVersion"/> to a Swashbuckle / Microsoft.AspNetCore.OpenApi
    /// document name. The same string is also used as the
    /// <c>IEndpointGroupNameMetadata.EndpointGroupName</c> attached to each chain by
    /// <see cref="ApiVersioningPolicy"/>. Defaults to <c>v{major}</c> for major.minor versions
    /// (e.g. <c>v1</c>, <c>v2</c>); falls back to <see cref="ApiVersion.ToString"/> for
    /// date-based versions.
    /// </summary>
    /// <remarks>
    /// Configure this strategy before calling <c>MapWolverineEndpoints</c>. The strategy is
    /// invoked once per versioned chain during policy application at startup; reassigning the
    /// property after startup has no effect on already-bound endpoints.
    /// </remarks>
    public Func<ApiVersion, string> DocumentNameStrategy { get; set; }
        = static v => v.MajorVersion is { } major
            ? "v" + major.ToString(CultureInfo.InvariantCulture)
            : v.ToString();
}
