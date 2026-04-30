using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Describes a discovered API version with the metadata needed to wire SwaggerUI / Scalar
/// version dropdowns. Returned by
/// <see cref="WolverineOpenApiEndpointRouteBuilderExtensions.DescribeWolverineApiVersions"/>.
/// </summary>
/// <param name="ApiVersion">The discovered API version.</param>
/// <param name="DocumentName">
/// Document name produced by
/// <see cref="WolverineApiVersioningOpenApiOptions.DocumentNameStrategy"/>; matches the
/// chain's <c>EndpointGroupName</c>.
/// </param>
/// <param name="DisplayName">Human-friendly label suitable for UI display (e.g. "API v1").</param>
/// <param name="IsDeprecated">
/// <see langword="true"/> when at least one chain at this version has an attribute-driven
/// <c>[ApiVersion(..., Deprecated = true)]</c> declaration or a configured
/// <see cref="DeprecationPolicy"/>.
/// </param>
/// <param name="SunsetPolicy">The configured sunset policy for this version, if any.</param>
public sealed record WolverineApiVersionDescription(
    ApiVersion ApiVersion,
    string DocumentName,
    string DisplayName,
    bool IsDeprecated,
    SunsetPolicy? SunsetPolicy);
