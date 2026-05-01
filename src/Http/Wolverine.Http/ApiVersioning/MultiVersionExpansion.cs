using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Expands handler chains that declare more than one API version (via multiple
/// <c>[ApiVersion]</c> attributes or <c>[MapToApiVersion]</c> filtering class-level versions)
/// into one chain per version. Runs before any <see cref="IHttpPolicy"/>, so middleware,
/// route prefix, and downstream policies apply uniformly to every clone.
/// </summary>
internal static class MultiVersionExpansion
{
    /// <summary>
    /// Walks <paramref name="chains"/>, replacing every multi-version chain with one clone per
    /// declared version. Single-version and unversioned chains are left untouched; the
    /// downstream <see cref="ApiVersioningPolicy"/> resolves them.
    /// </summary>
    public static void Expand(List<HttpChain> chains)
    {
        for (var i = chains.Count - 1; i >= 0; i--)
        {
            var chain = chains[i];
            if (chain.Method?.Method is null) continue;

            var versions = ApiVersionResolver.ResolveVersions(chain.Method.Method);
            if (versions.Count == 0) continue;

            if (versions.Count == 1)
            {
                // Single version: assign to the existing chain. This covers the
                // [MapToApiVersion("X")] case where filtering produced exactly one version,
                // and skips work for chains that already had ApiVersion set elsewhere.
                if (chain.ApiVersion is null)
                {
                    chain.ApiVersion = versions[0].Version;
                    if (versions[0].IsDeprecated && chain.DeprecationPolicy is null)
                    {
                        chain.DeprecationPolicy = new DeprecationPolicy();
                    }
                }
                continue;
            }

            chains.RemoveAt(i);
            // Insert clones at the original position so chains keep stable ordering.
            for (var j = versions.Count - 1; j >= 0; j--)
            {
                var resolution = versions[j];
                var clone = chain.CloneForVersion(resolution.Version, resolution.IsDeprecated);
                chains.Insert(i, clone);
            }
        }
    }
}
