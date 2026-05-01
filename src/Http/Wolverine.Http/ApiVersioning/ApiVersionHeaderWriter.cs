using System.Globalization;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Metadata record attached to the endpoint for each versioned chain, carrying the per-chain
/// version and optional sunset / deprecation policies so they can be read at request time
/// without per-chain code-gen arguments. This is part of the framework's observable contract
/// and is consumed by <see cref="ApiVersionHeaderWriter"/> at request time and by user-defined
/// OpenAPI filters (e.g., Swashbuckle <c>IOperationFilter</c>) for documentation generation.
/// </summary>
public sealed record ApiVersionEndpointHeaderState(
    ApiVersion Version,
    SunsetPolicy? Sunset,
    DeprecationPolicy? Deprecation);

/// <summary>
/// Singleton service that emits RFC 9745 <c>Deprecation</c>, RFC 8594 <c>Sunset</c>/<c>Link</c>,
/// and Asp.Versioning-style <c>api-supported-versions</c> response headers on versioned endpoints.
/// The per-chain state is read from <see cref="ApiVersionEndpointHeaderState"/> stored in the
/// endpoint metadata (set by <see cref="ApiVersioningPolicy"/>), so this writer can be a plain
/// singleton with no per-chain constructor arguments.
/// </summary>
/// <remarks>
/// Must remain public: Wolverine's dynamic code generation emits handler code at runtime that references
/// this type by name for postprocessor wiring. The generated code is in a separate assembly without
/// InternalsVisibleTo access to Wolverine.Http, so internal types are not accessible.
/// </remarks>
public sealed class ApiVersionHeaderWriter
{
    private readonly WolverineApiVersioningOptions _options;

    // Computed once on first request via Lazy<T>. Policies added to the options
    // dictionaries after the first request will not appear in this header. In normal
    // app startup all policies are registered before any HTTP request is processed,
    // so this is a safe optimization.
    private readonly Lazy<string> _supportedVersionsHeader;

    /// <summary>
    /// Initializes a new instance of <see cref="ApiVersionHeaderWriter"/>.
    /// </summary>
    /// <param name="options">The API versioning options used to compute the supported-versions header.</param>
    public ApiVersionHeaderWriter(WolverineApiVersioningOptions options)
    {
        _options = options;
        _supportedVersionsHeader = new Lazy<string>(() => BuildSupportedVersionsHeader(options));
    }

    /// <summary>
    /// Registers a <see cref="HttpResponse.OnStarting(Func{Task})"/> callback that writes the applicable
    /// versioning response headers immediately before the response headers are flushed to the client.
    /// Headers are emitted for every framework-produced response regardless of status code (2xx, 4xx,
    /// validation <c>ProblemDetails</c>, middleware short-circuits returning <c>IResult</c>). Responses
    /// produced by the global exception handler bypass the chain pipeline entirely and therefore never
    /// invoke this callback — wire deprecation headers on the exception path via separate middleware.
    /// </summary>
    /// <remarks>
    /// The method name remains <c>WriteAsync</c> because Wolverine's runtime code generation references
    /// it by name. It is invoked once per request, near the head of the chain's middleware list, before
    /// any status-branch divergence in the generated code.
    /// </remarks>
    /// <param name="context">The current HTTP context.</param>
    public Task WriteAsync(HttpContext context)
    {
        var state = context.GetEndpoint()?.Metadata.GetMetadata<ApiVersionEndpointHeaderState>();
        if (state is null)
            return Task.CompletedTask;

        context.Response.OnStarting(static stateObj =>
        {
            var (writer, ctx, hdrState) = ((ApiVersionHeaderWriter, HttpContext, ApiVersionEndpointHeaderState))stateObj;
            writer.ApplyHeaders(ctx, hdrState);
            return Task.CompletedTask;
        }, (this, context, state));

        return Task.CompletedTask;
    }

    private void ApplyHeaders(HttpContext context, ApiVersionEndpointHeaderState state)
    {
        var headers = context.Response.Headers;

        if (_options.EmitApiSupportedVersionsHeader && _supportedVersionsHeader.Value.Length > 0)
            headers["api-supported-versions"] = _supportedVersionsHeader.Value;

        if (_options.EmitDeprecationHeaders)
        {
            if (state.Deprecation is not null)
            {
                headers["Deprecation"] = state.Deprecation.Date is { } depDate
                    ? depDate.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)
                    : "true";
            }

            if (state.Sunset?.Date is { } sunsetDate)
                headers["Sunset"] = sunsetDate.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

            var links = BuildLinks(state.Sunset, state.Deprecation);
            if (links.Length > 0)
                headers["Link"] = links;
        }
    }

    private static string BuildSupportedVersionsHeader(WolverineApiVersioningOptions options)
    {
        var versions = options.SunsetPolicies.Keys
            .Concat(options.DeprecationPolicies.Keys)
            .Distinct()
            .OrderBy(v => v.MajorVersion ?? int.MaxValue)
            .ThenBy(v => v.MinorVersion ?? int.MaxValue)
            .Select(v => v.ToString())
            .ToArray();

        return versions.Length == 0 ? string.Empty : string.Join(", ", versions);
    }

    private static string BuildLinks(SunsetPolicy? sunset, DeprecationPolicy? deprecation)
    {
        var entries = new List<string>();

        if (sunset is not null)
            foreach (var link in sunset.Links)
                entries.Add(FormatLink(link, "sunset"));

        if (deprecation is not null)
            foreach (var link in deprecation.Links)
                entries.Add(FormatLink(link, "deprecation"));

        return entries.Count == 0 ? string.Empty : string.Join(", ", entries);
    }

    private static string FormatLink(LinkHeaderValue link, string rel)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('<').Append(link.LinkTarget).Append(">; rel=\"").Append(rel).Append('"');

        var title = link.Title.Value;
        if (!string.IsNullOrEmpty(title)) sb.Append("; title=\"").Append(title).Append('"');

        var type = link.Type.Value;
        if (!string.IsNullOrEmpty(type)) sb.Append("; type=\"").Append(type).Append('"');

        return sb.ToString();
    }
}
