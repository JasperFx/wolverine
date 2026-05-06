using System.ComponentModel;
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
/// <para>
/// Must remain public: Wolverine's dynamic code generation emits handler code at runtime that references
/// this type by name for postprocessor wiring. The generated code is in a separate assembly without
/// InternalsVisibleTo access to Wolverine.Http, so internal types are not accessible.
/// </para>
/// <para>
/// The class exposes two intentionally asymmetric entry points:
/// <see cref="WriteAsync(HttpContext)"/> is the chain-pipeline frame Wolverine's codegen calls automatically
/// for every versioned endpoint — its name is locked by codegen and its signature is locked to the
/// <c>HttpContext</c>-only convention. <see cref="WriteVersioningHeadersTo(HttpContext, ApiVersionEndpointHeaderState)"/>
/// is a synchronous helper for advanced scenarios such as exception-handler middleware that
/// needs to emit the same RFC headers on the 5xx exception path (where the chain pipeline has been bypassed).
/// </para>
/// </remarks>
public sealed class ApiVersionHeaderWriter
{
    private readonly WolverineApiVersioningOptions _options;

    // Computed once on first request via Lazy<T>. Policies added to the options
    // dictionaries after the first request will not appear in this fallback header.
    // The fallback only applies to chains whose endpoint has no ApiVersionMetadata
    // (i.e. chains not produced by ApiVersioningPolicy's per-clone wiring); in normal
    // app startup all policies are registered before any HTTP request is processed,
    // so this is a safe optimization.
    private readonly Lazy<string> _fallbackSupportedVersionsHeader;

    /// <summary>
    /// Initializes a new instance of <see cref="ApiVersionHeaderWriter"/>.
    /// </summary>
    /// <param name="options">The API versioning options used to compute the supported-versions header.</param>
    public ApiVersionHeaderWriter(WolverineApiVersioningOptions options)
    {
        _options = options;
        _fallbackSupportedVersionsHeader = new Lazy<string>(() => BuildFallbackSupportedVersionsHeader(options));
    }

    /// <summary>
    /// Registers a <see cref="HttpResponse.OnStarting(Func{Task})"/> callback that writes the applicable
    /// versioning response headers immediately before the response headers are flushed to the client.
    /// Headers are emitted for every framework-produced response regardless of status code (2xx, 4xx,
    /// validation <c>ProblemDetails</c>, middleware short-circuits returning <c>IResult</c>). Responses
    /// produced by the global exception handler bypass the chain pipeline entirely and therefore never
    /// invoke this callback — wire deprecation headers on the exception path via separate middleware.
    /// The <c>api-supported-versions</c> header reads from the endpoint's
    /// <see cref="ApiVersionMetadata"/> (seeded by <see cref="ApiVersioningPolicy"/> with the
    /// full sibling union for chains at the same <c>(verb, route-after-strip-prefix)</c>),
    /// falling back to the options-driven sunset/deprecation key union when no metadata is
    /// present on the endpoint.
    /// </summary>
    /// <remarks>
    /// The method name remains <c>WriteAsync</c> because Wolverine's runtime code generation references
    /// it by name. It is invoked once per request as the first frame of the chain, before any
    /// status-branch divergence in the generated code.
    /// </remarks>
    /// <param name="context">The current HTTP context.</param>
    public Task WriteAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var state = endpoint?.Metadata.GetMetadata<ApiVersionEndpointHeaderState>();
        if (state is null)
            return Task.CompletedTask;

        // Capture this + context: writer is already resolved from DI by the generated handler
        // (Wolverine codegen via MethodCall.For), so no service location is needed in the callback.
        // One closure allocation per request matches the cost ASP.NET Core middleware pays for OnStarting.
        context.Response.OnStarting(() =>
        {
            // Re-fetch inside OnStarting because the endpoint can be re-routed by middleware between this frame and header-flush time.
            var hdrState = context.GetEndpoint()?.Metadata.GetMetadata<ApiVersionEndpointHeaderState>();
            if (hdrState is not null)
                ApplyHeaders(context, hdrState);
            return Task.CompletedTask;
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the applicable RFC 9745 / RFC 8594 / RFC 8288 response headers (<c>Deprecation</c>,
    /// <c>Sunset</c>, <c>Link</c>, <c>api-supported-versions</c>) to <paramref name="context"/>.<see cref="HttpContext.Response"/>
    /// based on the supplied per-endpoint <paramref name="state"/>. Public so application code on the
    /// exception path (e.g. a custom <c>UseExceptionHandler</c> middleware) can emit the same headers
    /// the chain pipeline would have written for non-exception responses.
    /// </summary>
    /// <param name="context">The current HTTP context whose response headers will be written.</param>
    /// <param name="state">The per-endpoint header state, typically read from
    /// <c>context.GetEndpoint()?.Metadata.GetMetadata&lt;ApiVersionEndpointHeaderState&gt;()</c>.</param>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public void WriteVersioningHeadersTo(HttpContext context, ApiVersionEndpointHeaderState state)
        => ApplyHeaders(context, state);

    private void ApplyHeaders(HttpContext context, ApiVersionEndpointHeaderState state)
    {
        var headers = context.Response.Headers;

        if (_options.EmitApiSupportedVersionsHeader)
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is not null)
            {
                var supportedHeader = BuildSupportedVersionsHeader(endpoint);
                if (supportedHeader.Length > 0)
                    headers["api-supported-versions"] = supportedHeader;
            }
        }

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

    /// <summary>
    /// Build the <c>api-supported-versions</c> header value for a single request. The endpoint's
    /// <see cref="ApiVersionMetadata"/> is the authoritative source — it carries the full sibling
    /// union assembled by <see cref="ApiVersioningPolicy"/> at startup, so the header reflects every
    /// version that serves the same <c>(verb, route-after-strip-prefix)</c>, supported and
    /// deprecated alike (matching the Asp.Versioning convention of reporting
    /// <c>ImplementedApiVersions</c>). Falls back to the options-driven union for chains that have
    /// no per-endpoint metadata (e.g. chains wired up outside the policy pipeline).
    /// </summary>
    private string BuildSupportedVersionsHeader(Endpoint endpoint)
    {
        var metadata = endpoint.Metadata.GetMetadata<ApiVersionMetadata>();
        if (metadata is null)
            return _fallbackSupportedVersionsHeader.Value;

        var model = metadata.Map(ApiVersionMapping.Explicit);
        var versions = model.ImplementedApiVersions;
        if (versions.Count == 0)
            return _fallbackSupportedVersionsHeader.Value;

        return string.Join(", ", versions
            .OrderBy(v => v.MajorVersion ?? int.MaxValue)
            .ThenBy(v => v.MinorVersion ?? int.MaxValue)
            .Select(v => v.ToString()));
    }

    private static string BuildFallbackSupportedVersionsHeader(WolverineApiVersioningOptions options)
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
