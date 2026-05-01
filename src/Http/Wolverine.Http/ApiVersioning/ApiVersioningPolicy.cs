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

    /// <summary>
    /// Chains for which Step G attached <see cref="ApiVersionEndpointHeaderState"/> metadata, exposed
    /// for <see cref="ApiVersionHeaderFinalizationPolicy"/> to position the writer call at index 0
    /// after all other user-supplied policies have run.
    /// </summary>
    internal IReadOnlyCollection<HttpChain> ChainsRequiringHeaderWriter => _headerProcessedChains;

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

    /// <summary>Step A — read <c>[ApiVersion]</c> / <c>[ApiVersionNeutral]</c> from the handler method and propagate to the chain.</summary>
    private static void ResolveAttributes(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.Method?.Method is null)
                continue;

            // Single reflection pass — resolves neutrality and validates that [ApiVersion] +
            // [ApiVersionNeutral] are not both declared on the same target (throws on conflict).
            // Method-level wins over class-level in both directions.
            if (ApiVersionNeutralResolver.Resolve(chain.Method.Method))
            {
                chain.IsApiVersionNeutral = true;
                // Clear any prior fluent HasApiVersion(...) assignment — a method-level
                // [ApiVersionNeutral] overriding a versioned class must not leave a stale version
                // on the chain that DetectDuplicateRoutes / RewriteRoutes would later observe.
                chain.ApiVersion = null;
                continue;
            }

            var resolution = ApiVersionResolver.Resolve(chain.Method.Method);
            if (resolution is null)
                continue;

            if (chain.ApiVersion is null)
                chain.ApiVersion = resolution.Value.Version;

            if (resolution.Value.IsDeprecated && chain.DeprecationPolicy is null)
                chain.DeprecationPolicy = new DeprecationPolicy();
        }
    }

    /// <summary>Step B — handle chains still missing a version per the configured fallback rule.
    /// Chains carrying <see cref="HttpChain.IsApiVersionNeutral"/> are treated as having made an
    /// explicit version-neutral choice, so they are exempt from <see cref="UnversionedPolicy.RequireExplicit"/>
    /// and <see cref="UnversionedPolicy.AssignDefault"/>.</summary>
    private void ApplyUnversionedPolicy(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is not null || chain.IsApiVersionNeutral)
                continue;

            switch (_options.UnversionedPolicy)
            {
                case UnversionedPolicy.PassThrough:
                    break;

                case UnversionedPolicy.RequireExplicit:
                    throw new InvalidOperationException(
                        $"Endpoint '{Identify(chain)}' does not declare an [ApiVersion] attribute. " +
                        $"The current UnversionedPolicy is '{UnversionedPolicy.RequireExplicit}', which requires every endpoint " +
                        "to carry an explicit version. To opt an endpoint out of versioning, mark it with [ApiVersionNeutral].");

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

    /// <summary>Step D — fail fast when two chains collide. Versioned chains collide on
    /// <c>(verb, route, version)</c>; neutral chains collide on <c>(verb, route)</c> alone, since
    /// they are not partitioned by version. Without this second check, two neutral chains at the
    /// same route would both register and ASP.NET Core would throw an opaque routing error at
    /// the first request.</summary>
    private static void DetectDuplicateRoutes(IReadOnlyList<HttpChain> chains)
    {
        DetectConflicts(
            chains,
            include: c => c.ApiVersion is not null,
            keyOf: c => (
                Verb: c.HttpMethods.FirstOrDefault() ?? "",
                Route: c.RoutePattern?.RawText ?? "",
                Version: c.ApiVersion!.ToString()),
            describe: (key, names) =>
                $"Duplicate endpoint registration detected: " +
                $"[{key.Verb}] '{key.Route}' at version '{key.Version}'. " +
                $"Conflicting chains: {names}");

        DetectConflicts(
            chains,
            include: c => c.IsApiVersionNeutral,
            keyOf: c => (
                Verb: c.HttpMethods.FirstOrDefault() ?? "",
                Route: c.RoutePattern?.RawText ?? ""),
            describe: (key, names) =>
                $"Duplicate version-neutral endpoint registration detected: " +
                $"[{key.Verb}] '{key.Route}'. " +
                $"Version-neutral chains are not partitioned by version, so two chains at the " +
                $"same (verb, route) collide unconditionally. Conflicting chains: {names}");
    }

    private static void DetectConflicts<TKey>(
        IReadOnlyList<HttpChain> chains,
        Func<HttpChain, bool> include,
        Func<HttpChain, TKey> keyOf,
        Func<TKey, string, string> describe)
    {
        var conflicts = chains
            .Where(include)
            .GroupBy(keyOf)
            .Where(g => g.Count() > 1);

        foreach (var conflict in conflicts)
        {
            var names = string.Join(", ", conflict.Select(Identify));
            throw new InvalidOperationException(describe(conflict.Key, names));
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
    /// Version-neutral chains receive <see cref="ApiVersionMetadata.Neutral"/> so consumers of the
    /// metadata graph (Asp.Versioning tooling, the Swashbuckle filter) can recognise them, but they
    /// deliberately get no <c>IEndpointGroupNameMetadata</c>. Without a group name they are skipped
    /// by Swashbuckle's default group-name partitioning; users opt them into versioned documents
    /// from <c>DocInclusionPredicate</c> (see <c>versioning.md</c>).</summary>
    private void AttachMetadata(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            // Mirror ApplyUnversionedPolicy: deal with the neutral branch first so the intent of
            // each branch is obvious. The _processedChains guard then prevents double-attachment
            // of versioned metadata if Apply() is called twice on the same chain.
            if (chain.IsApiVersionNeutral)
            {
                if (!_processedChains.Add(chain))
                    continue;

                chain.Metadata.WithMetadata(ApiVersionMetadata.Neutral);

                // Two neutral chains can share the same handler-method name (e.g. two classes
                // each declaring a method called Get). Without an explicit OperationId, ASP.NET
                // Core derives EndpointName from the route pattern, and two neutral handlers at
                // different routes still hit a duplicate-name collision because the underlying
                // ToString() is not unique per chain. Set the OperationId — already unique per
                // handler type + method — as the explicit endpoint name, just like versioned chains.
                EnsureExplicitOperationId(chain);

                continue;
            }

            if (!_processedChains.Add(chain))
                continue;

            if (chain.ApiVersion is null)
                continue;

            var groupName = _options.OpenApi.DocumentNameStrategy(chain.ApiVersion);
            chain.Metadata.WithGroupName(groupName);

            var model = new ApiVersionModel(chain.ApiVersion);
            chain.Metadata.WithMetadata(new ApiVersionMetadata(model, model));

            // Make the OperationId (already unique per handler type + method) the explicit
            // endpoint name. Without this, ASP.NET Core uses ToString() which is derived from
            // the original route pattern and collides when multiple versions share the same
            // route template (e.g. [WolverineGet("/orders")] on three different classes).
            EnsureExplicitOperationId(chain);
        }
    }

    private static void EnsureExplicitOperationId(HttpChain chain)
    {
        if (!chain.HasExplicitOperationId)
            chain.SetExplicitOperationId(chain.OperationId);
    }

    /// <summary>
    /// Step G — attach the per-chain <see cref="ApiVersionEndpointHeaderState"/> metadata that the
    /// writer reads at request time. The actual <c>chain.Middleware.Insert(0, …)</c> for the writer
    /// itself is deferred to <see cref="ApiVersionHeaderFinalizationPolicy"/>, which is registered
    /// at the end of <c>MapWolverineEndpoints</c> so it executes after every user-supplied policy
    /// (notably FluentValidation, which itself inserts a short-circuiting frame at index 0). Doing
    /// the insert here would leave the writer below those frames and the OnStarting hook would not
    /// register before <c>return;</c> on the validation-fail path.
    /// </summary>
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
        }
    }

    private bool RequiresHeaderWriter(HttpChain chain) =>
        chain.SunsetPolicy is not null
        || chain.DeprecationPolicy is not null
        || _options.EmitApiSupportedVersionsHeader;

    private static string Identify(HttpChain chain) =>
        chain.DisplayName
        ?? (chain.Method?.Method?.DeclaringType?.FullName + "." + chain.Method?.Method?.Name)
        ?? "(unknown)";
}
