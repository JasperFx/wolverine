using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.AspVersioning;

/// <summary>
/// Encapsulates an <see cref="HttpChain"/>, its version-neutral status, and its API version buckets
/// (supported, deprecated, advertised, and advertised deprecated).
/// </summary>
internal sealed class VersionedChain
{
    public VersionedChain(
        HttpChain chain,
        IReadOnlyCollection<ApiVersionResolution> versions,
        IReadOnlyCollection<ApiVersionResolution> advertised,
        bool isVersionNeutral
    )
    {
        Chain = chain;
        IsVersionNeutral = isVersionNeutral;
        HasVersioningInfo = isVersionNeutral || versions.Count > 0 || advertised.Count > 0;

        Supported = [.. versions.Where(r => !r.IsDeprecated)];
        Deprecated = [.. versions.Where(r => r.IsDeprecated)];
        Advertised = [.. advertised.Where(r => !r.IsDeprecated)];
        AdvertisedDeprecated = [.. advertised.Where(r => r.IsDeprecated)];
    }

    public HttpChain Chain { get; }
    public bool HasVersioningInfo { get; }
    public bool IsVersionNeutral { get; }

    public IReadOnlyList<ApiVersionResolution> Supported { get; }
    public IReadOnlyList<ApiVersionResolution> Deprecated { get; }
    public IReadOnlyList<ApiVersionResolution> Advertised { get; }
    public IReadOnlyList<ApiVersionResolution> AdvertisedDeprecated { get; }

    public static VersionedChain FromHttpChain(HttpChain chain) =>
        new(
            chain,
            ApiVersionResolver.ResolveVersions(chain.Method.Method),
            AdvertisedVersionResolver.ResolveAdvertised(chain.Method.Method),
            ApiVersionNeutralResolver.Resolve(chain.Method.Method)
        );
}
