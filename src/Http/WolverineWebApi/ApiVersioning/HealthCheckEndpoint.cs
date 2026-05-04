using Asp.Versioning;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

#region sample_api_version_neutral_endpoint
/// <summary>
/// Class-level <c>[ApiVersionNeutral]</c> endpoint. Wolverine keeps the declared route
/// (<c>/health</c>), skips URL-segment rewriting, omits version-related response headers,
/// and exempts the chain from <c>UnversionedPolicy.RequireExplicit</c>.
/// </summary>
[ApiVersionNeutral]
public static class HealthCheckEndpoint
{
    [WolverineGet("/health", OperationId = "HealthCheckEndpoint.Get")]
    public static HealthCheckResponse Get() => new("ok");
}

public record HealthCheckResponse(string Status);
#endregion
