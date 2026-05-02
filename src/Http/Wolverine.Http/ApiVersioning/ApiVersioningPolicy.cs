using Asp.Versioning;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// An <see cref="IHttpPolicy"/> that applies API versioning semantics to every
/// <see cref="HttpChain"/> during bootstrapping. Steps run in order:
/// <list type="bullet">
///   <item><description>A — Resolve <c>[ApiVersion]</c> attributes on handler methods.</description></item>
///   <item><description>B — Apply <see cref="UnversionedPolicy"/> to chains that remain unversioned.</description></item>
///   <item><description>C — Attach sunset / deprecation policies from <see cref="WolverineApiVersioningOptions"/>.</description></item>
///   <item><description>D — Reject duplicate (verb, route, version) triples.</description></item>
///   <item><description>E — Rewrite route patterns with the URL-segment version prefix.</description></item>
///   <item><description>F — Attach group-name and <c>Asp.Versioning.ApiVersionMetadata</c> to the endpoint.</description></item>
///   <item><description>G — Wire the response-header postprocessor on chains that need it.</description></item>
/// </list>
/// </summary>
internal sealed class ApiVersioningPolicy : IHttpPolicy
{
    private readonly WolverineApiVersioningOptions _options;
    private readonly HashSet<HttpChain> _processedChains = new();
    private readonly HashSet<HttpChain> _headerProcessedChains = new();

    /// <summary>Initializes a new instance of <see cref="ApiVersioningPolicy"/>.</summary>
    /// <param name="options">The API versioning options that drive this policy's behaviour.</param>
    public ApiVersioningPolicy(WolverineApiVersioningOptions options)
    {
        _options = options;
    }

    /// <inheritdoc/>
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        ResolveAttributes(chains);
        ApplyUnversionedPolicy(chains);
        ApplyOptionsPolicies(chains);
        DetectDuplicateRoutes(chains);
        RewriteRoutes(chains);
        AttachMetadata(chains);
        WireHeaderPostprocessors(chains);
    }

    /// <summary>Step A — read <c>[ApiVersion]</c> from the handler method and propagate to the chain.
    /// Multi-version expansion runs earlier in <see cref="HttpGraph.DiscoverEndpoints"/>, so chains
    /// reaching this step have either no version or one already set by the expansion. Index the
    /// resolver result explicitly after a count check so the <c>default(ApiVersionResolution)</c>
    /// foot-gun (a struct with a null <c>Version</c>) is not relied on for the empty case.</summary>
    private static void ResolveAttributes(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.Method?.Method is null)
                continue;

            // Chains produced by multi-version expansion already have ApiVersion assigned;
            // skip resolver work to avoid throwing on the still-multi-version method attributes.
            if (chain.ApiVersion is not null)
                continue;

            var versions = ApiVersionResolver.ResolveVersions(chain.Method.Method);
            if (versions.Count == 0)
                continue;

            var resolution = versions[0];
            chain.ApiVersion = resolution.Version;

            if (resolution.IsDeprecated && chain.DeprecationPolicy is null)
                chain.DeprecationPolicy = new DeprecationPolicy();
        }
    }

    /// <summary>Step B — handle chains still missing a version per the configured fallback rule.</summary>
    private void ApplyUnversionedPolicy(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is not null)
                continue;

            switch (_options.UnversionedPolicy)
            {
                case UnversionedPolicy.PassThrough:
                    break;

                case UnversionedPolicy.RequireExplicit:
                    throw new InvalidOperationException(
                        $"Endpoint '{Identify(chain)}' does not declare an [ApiVersion] attribute. " +
                        $"The current UnversionedPolicy is '{UnversionedPolicy.RequireExplicit}', which requires every endpoint " +
                        "to carry an explicit version.");

                case UnversionedPolicy.AssignDefault:
                    chain.ApiVersion = _options.DefaultVersion
                        ?? throw new InvalidOperationException(
                            "DefaultVersion must be set when UnversionedPolicy is AssignDefault.");
                    break;
            }
        }
    }

    /// <summary>Step C — apply sunset / deprecation policies from options without overwriting attribute-driven values.</summary>
    private void ApplyOptionsPolicies(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null)
                continue;

            if (chain.SunsetPolicy is null && _options.SunsetPolicies.TryGetValue(chain.ApiVersion, out var sunset))
                chain.SunsetPolicy = sunset;

            if (chain.DeprecationPolicy is null && _options.DeprecationPolicies.TryGetValue(chain.ApiVersion, out var dep))
                chain.DeprecationPolicy = dep;
        }
    }

    /// <summary>Step D — fail fast when two chains share <c>(verb, route, version)</c>.</summary>
    private static void DetectDuplicateRoutes(IReadOnlyList<HttpChain> chains)
    {
        var conflicts = chains
            .Where(c => c.ApiVersion is not null)
            .GroupBy(c => (
                Verb: c.HttpMethods.FirstOrDefault() ?? "",
                Route: c.RoutePattern?.RawText ?? "",
                Version: c.ApiVersion!.ToString()))
            .Where(g => g.Count() > 1);

        foreach (var conflict in conflicts)
        {
            // Use OperationId here (rather than the shared DisplayName) so the diagnostic names
            // every conflicting clone individually — the version-suffixed OperationIds make each
            // clone uniquely identifiable when sibling clones across distinct handler classes
            // collide at the same (verb, route, version) triple.
            var names = string.Join(", ", conflict.Select(c => c.OperationId));
            throw new InvalidOperationException(
                $"Duplicate endpoint registration detected: " +
                $"[{conflict.Key.Verb}] '{conflict.Key.Route}' at version '{conflict.Key.Version}'. " +
                $"Conflicting chains: {names}");
        }
    }

    /// <summary>Step E — prepend the URL-segment version prefix to every versioned chain.</summary>
    private void RewriteRoutes(IReadOnlyList<HttpChain> chains)
    {
        if (_options.UrlSegmentPrefix is null)
            return;

        ValidateUrlSegmentPrefix(chains);

        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null || chain.RoutePattern is null)
                continue;

            RewriteRouteForChain(chain);
        }
    }

    private void ValidateUrlSegmentPrefix(IReadOnlyList<HttpChain> chains)
    {
        if (_options.UrlSegmentPrefix!.Contains("{version}", StringComparison.Ordinal))
            return;

        if (!chains.Any(c => c.ApiVersion is not null))
            return;

        throw new InvalidOperationException(
            $"WolverineApiVersioningOptions.UrlSegmentPrefix is set to '{_options.UrlSegmentPrefix}' which does not contain the required '{{version}}' token. All versioned endpoints would map to the same URL prefix. Set UrlSegmentPrefix to null to disable URL-segment versioning, or include '{{version}}' in the prefix template (e.g. 'v{{version}}' or 'api/v{{version}}').");
    }

    private void RewriteRouteForChain(HttpChain chain)
    {
        var expectedPrefix = BuildExpectedPrefix(chain.ApiVersion!);
        var currentRoute = chain.RoutePattern!.RawText ?? string.Empty;

        // Idempotency guard: skip if the chain is already prefixed.
        if (currentRoute == expectedPrefix ||
            currentRoute.StartsWith(expectedPrefix + "/", StringComparison.Ordinal))
        {
            return;
        }

        var trimmed = currentRoute.TrimStart('/');
        var newRoute = string.IsNullOrEmpty(trimmed) ? expectedPrefix : $"{expectedPrefix}/{trimmed}";
        chain.RoutePattern = RoutePatternFactory.Parse(newRoute);
    }

    private string BuildExpectedPrefix(ApiVersion version)
    {
        var versionSegment = _options.UrlSegmentVersionFormatter(version);
        return "/" + _options.UrlSegmentPrefix!.Replace("{version}", versionSegment).TrimStart('/');
    }

    /// <summary>Step F — attach group-name, ApiVersionMetadata, and ensure unique endpoint names.
    /// The <c>ApiVersionMetadata</c> model is seeded with the union of versions implemented at the
    /// same (verb, route) pair so the <c>api-supported-versions</c> response header reports every
    /// sibling clone, not just this clone's own version.</summary>
    /// <remarks>
    /// The sibling grouping key is <c>(verb, route-after-strip-prefix)</c>, NOT
    /// <c>(verb, route-after-strip-prefix, handler-type)</c>. Chains from distinct handler classes
    /// that publish the same logical route are merged into one sibling set. This matches the
    /// Asp.Versioning convention where any chain at the route is part of the same logical version
    /// set regardless of which class declared which version (e.g.
    /// <c>OrdersV1V2Endpoint</c> declaring v1+v2 and <c>OrdersV3Endpoint</c> declaring v3 at the
    /// same <c>(GET, /orders)</c> route are merged into one sibling chain advertising 1.0/2.0/3.0
    /// in <c>api-supported-versions</c>). The <c>cross_class_chains_at_same_route_share_supported_versions</c>
    /// integration test pins this behaviour.
    /// </remarks>
    private void AttachMetadata(IReadOnlyList<HttpChain> chains)
    {
        // Group versioned chains by (verb, route-without-version-prefix). Two chains in the same
        // group are siblings — typically multi-version clones, but also any chains that happen to
        // share a verb and the post-strip route. Each clone's model advertises the full sibling set
        // as supported / deprecated so the response header consumers see the union.
        var siblingsByKey = new Dictionary<(string Verb, string Route), List<HttpChain>>();
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null) continue;

            var key = (
                Verb: chain.HttpMethods.FirstOrDefault() ?? "",
                Route: StripVersionPrefix(chain));

            if (!siblingsByKey.TryGetValue(key, out var bucket))
            {
                bucket = new List<HttpChain>();
                siblingsByKey[key] = bucket;
            }
            bucket.Add(chain);
        }

        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null || !_processedChains.Add(chain))
                continue;

            var groupName = _options.OpenApi.DocumentNameStrategy(chain.ApiVersion);
            chain.Metadata.WithGroupName(groupName);

            var key = (
                Verb: chain.HttpMethods.FirstOrDefault() ?? "",
                Route: StripVersionPrefix(chain));

            var siblings = siblingsByKey[key];
            var supported = siblings
                .Where(s => s.DeprecationPolicy is null)
                .Select(s => s.ApiVersion!)
                .Distinct()
                .ToArray();
            var deprecated = siblings
                .Where(s => s.DeprecationPolicy is not null)
                .Select(s => s.ApiVersion!)
                .Distinct()
                .ToArray();

            var model = new ApiVersionModel(
                declaredVersions: new[] { chain.ApiVersion },
                supportedVersions: supported,
                deprecatedVersions: deprecated,
                advertisedVersions: Array.Empty<ApiVersion>(),
                deprecatedAdvertisedVersions: Array.Empty<ApiVersion>());
            chain.Metadata.WithMetadata(new ApiVersionMetadata(model, model));

            // Make the OperationId (already unique per handler type + method) the explicit
            // endpoint name. Without this, ASP.NET Core uses ToString() which is derived from
            // the original route pattern and collides when multiple versions share the same
            // route template (e.g. [WolverineGet("/orders")] on three different classes).
            if (!chain.HasExplicitOperationId)
                chain.SetExplicitOperationId(chain.OperationId);
        }
    }

    /// <summary>Removes the URL-segment version prefix (if one was injected by <see cref="RewriteRoutes"/>)
    /// from the chain's current route, returning the trailing portion that is identical across all
    /// sibling versions. When <see cref="WolverineApiVersioningOptions.UrlSegmentPrefix"/> is null
    /// the original route is returned unchanged.</summary>
    private string StripVersionPrefix(HttpChain chain)
    {
        var route = chain.RoutePattern?.RawText ?? string.Empty;
        if (_options.UrlSegmentPrefix is null) return route;

        var prefix = BuildExpectedPrefix(chain.ApiVersion!);
        if (route == prefix) return string.Empty;
        if (route.StartsWith(prefix + "/", StringComparison.Ordinal))
            return route.Substring(prefix.Length);

        return route;
    }

    /// <summary>Step G — register the response-header postprocessor for chains that emit headers.</summary>
    private void WireHeaderPostprocessors(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null || !RequiresHeaderWriter(chain))
                continue;

            if (!_headerProcessedChains.Add(chain))
                continue;

            // Per-chain state lives on endpoint metadata so the singleton writer can read it at request time.
            var state = new ApiVersionEndpointHeaderState(chain.ApiVersion, chain.SunsetPolicy, chain.DeprecationPolicy);
            chain.Metadata.WithMetadata(state);

            // MethodCall has no .Target — Wolverine codegen resolves ApiVersionHeaderWriter from DI
            // at request time, then satisfies HttpContext from the request scope.
            chain.Postprocessors.Add(MethodCall.For<ApiVersionHeaderWriter>(x => x.WriteAsync(null!)));
        }
    }

    private bool RequiresHeaderWriter(HttpChain chain) =>
        chain.SunsetPolicy is not null
        || chain.DeprecationPolicy is not null
        || _options.EmitApiSupportedVersionsHeader;

    /// <summary>
    /// Diagnostic identifier for a chain in error messages from the unversioned-policy and other
    /// non-clone code paths. Prefers <see cref="HttpChain.DisplayName"/> so consumer-friendly
    /// labels (e.g. <c>"GET /orders (unversioned)"</c>) are preserved verbatim. The duplicate-route
    /// detector in <see cref="DetectDuplicateRoutes"/> intentionally uses
    /// <see cref="HttpChain.OperationId"/> instead because clones share a DisplayName but have
    /// version-suffixed OperationIds.
    /// </summary>
    private static string Identify(HttpChain chain) =>
        chain.DisplayName
        ?? (chain.Method?.Method?.DeclaringType?.FullName + "." + chain.Method?.Method?.Name)
        ?? "(unknown)";
}
