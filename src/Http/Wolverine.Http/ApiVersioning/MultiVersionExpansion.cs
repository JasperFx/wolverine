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
    /// Mutates <paramref name="chains"/> in place: every chain whose handler declares more than
    /// one API version is removed and replaced with one clone per declared version. Single-version
    /// and unversioned chains are left untouched; the downstream <see cref="ApiVersioningPolicy"/>
    /// resolves them via <see cref="ApiVersionResolver.ResolveVersions"/>.
    /// </summary>
    public static void ExpandInPlace(List<HttpChain> chains)
    {
        for (var i = chains.Count - 1; i >= 0; i--)
        {
            var chain = chains[i];
            if (chain.Method?.Method is null) continue;

            var versions = ApiVersionResolver.ResolveVersions(chain.Method.Method);
            if (versions.Count < 2) continue;

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
