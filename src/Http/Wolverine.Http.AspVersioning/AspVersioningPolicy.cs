using Asp.Versioning;
using Asp.Versioning.Builder;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wolverine.Http.AspVersioning;

internal sealed class AspVersioningPolicy : IHttpPolicy
{
    // Chains this policy instance has already attached versioning metadata to. Guards against a
    // second Apply(...) over the same chains re-attaching a duplicate ApiVersionSet or re-emitting
    // MapToApiVersion. Mirrors the native ApiVersioningPolicy._processedChains guard.
    private readonly HashSet<HttpChain> _processedChains = [];

    public void Apply(
        IReadOnlyList<HttpChain> chains,
        GenerationRules rules,
        IServiceContainer container
    )
    {
        validateConfiguration(container);

        var versionedChainGroups = chains
            .Select(VersionedChain.FromHttpChain)
            .Where(c => c.HasVersioningInfo)
            .GroupBy(
                c => normalizeRoute(c.Chain.RoutePattern?.RawText),
                StringComparer.OrdinalIgnoreCase
            );

        foreach (var group in versionedChainGroups)
            applyVersioningMetadata([.. group]);
    }

    /// <summary>
    /// Applies versioning metadata to a group of versioned chains that share the same normalized route.
    /// </summary>
    /// <param name="versionedChains">The versioned chains to apply metadata to.</param>
    private void applyVersioningMetadata(IReadOnlyList<VersionedChain> versionedChains)
    {
        var versionSet = buildVersionSet(versionedChains);

        foreach (var vc in versionedChains)
        {
            // Idempotency: never touch a chain twice. Without this, a repeated Apply would attach a
            // second WithApiVersionSet convention (the finalizer then sees two sets) and duplicate
            // MapToApiVersion calls.
            if (!_processedChains.Add(vc.Chain))
                continue;

            vc.Chain.RequiresApplicationServices = true;

            if (!vc.Chain.HasExplicitOperationId)
                vc.Chain.SetExplicitOperationId(vc.Chain.OperationId);

            vc.Chain.WithApiVersionSet(versionSet);

            if (vc.IsVersionNeutral)
            {
                vc.Chain.IsApiVersionNeutral();
                continue;
            }

            // Map this chain to every version it serves. Supported-vs-deprecated and advertised are
            // all properties of the shared set built above — MapToApiVersion inherits roles from the
            // set and (unlike HasApiVersion/HasDeprecatedApiVersion) does not mutate it, so there is
            // no per-chain role reconciliation. Advertise-only chains serve nothing, emit nothing,
            // and inherit the set's full space (incl. the advertised fold).
            foreach (var served in vc.Supported.Concat(vc.Deprecated))
                vc.Chain.MapToApiVersion(served.Version);
        }
    }

    /// <summary>
    /// Builds an <see cref="ApiVersionSet"/> to express the versioning semantics of a group of versioned
    /// chains that share the same normalized route.
    /// </summary>
    /// <param name="versionedChains">The versioned chains used to build the set.</param>
    /// <returns>The constructed <see cref="ApiVersionSet"/>.</returns>
    private static ApiVersionSet buildVersionSet(IReadOnlyList<VersionedChain> versionedChains)
    {
        // Pass null so that we don't clutter the user's OpenAPI docs with internal tags
        var versionSetBuilder = new ApiVersionSetBuilder(null);

        var supported = versionedChains
            .SelectMany(vc => vc.Supported)
            .Select(r => r.Version)
            .ToHashSet();
        var deprecated = versionedChains
            .SelectMany(vc => vc.Deprecated)
            .Select(r => r.Version)
            .ToHashSet();
        var advertised = versionedChains
            .SelectMany(vc => vc.Advertised)
            .Select(r => r.Version)
            .ToHashSet();
        var advertisedDeprecated = versionedChains
            .SelectMany(vc => vc.AdvertisedDeprecated)
            .Select(r => r.Version)
            .ToHashSet();

        // Supported wins over deprecated for the same version anywhere in the group — a version supported
        // (served or advertised) by any sibling must not also be reported deprecated.
        var allSupported = new HashSet<ApiVersion>(supported);
        allSupported.UnionWith(advertised);
        deprecated.ExceptWith(allSupported);
        advertisedDeprecated.ExceptWith(allSupported);

        // Seed each version under its role so the set model keeps the advertised lists distinct
        // (AdvertisedApiVersions / DeprecatedAdvertisedApiVersions) while still folding them into
        // SupportedApiVersions / DeprecatedApiVersions for the response headers.
        foreach (var version in supported)
            versionSetBuilder.HasApiVersion(version);

        foreach (var version in deprecated)
            versionSetBuilder.HasDeprecatedApiVersion(version);

        foreach (var version in advertised)
            versionSetBuilder.AdvertisesApiVersion(version);

        foreach (var version in advertisedDeprecated)
            versionSetBuilder.AdvertisesDeprecatedApiVersion(version);

        return versionSetBuilder.Build();
    }

    // Fail fast if Asp.Versioning is not configured or if the user has also enabled Wolverine's
    // native API versioning.
    private static void validateConfiguration(IServiceContainer container)
    {
        if (container.Services.GetService<IOptions<ApiVersioningOptions>>() is null)
            throw new InvalidOperationException(
                "Asp.Versioning is not configured. Ensure that you call `AddApiVersioning()` "
                    + "when configuring your application services."
            );

        if (container.Services.GetRequiredService<WolverineHttpOptions>().ApiVersioning is not null)
            throw new InvalidOperationException(
                "Wolverine's native API versioning (`UseApiVersioning()`) and "
                    + "Asp.Versioning integration (`UseAspVersioning()`) cannot be enabled simultaneously. "
                    + "Please choose one or the other."
            );
    }

    // Route grouping key: OrdinalIgnoreCase after trimming leading/trailing slashes. A null, empty,
    // or whitespace route — and the root route "/" — all normalize to the empty string.
    private static string normalizeRoute(string? rawRoutePattern) =>
        string.IsNullOrWhiteSpace(rawRoutePattern) ? string.Empty : rawRoutePattern.Trim('/');
}
