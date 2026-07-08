using Asp.Versioning;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.AspVersioning.Tests;

internal static class HttpChainExtensions
{
    /// <summary>Materialize the endpoint and read the attached <see cref="ApiVersionMetadata"/> (null when none).</summary>
    public static ApiVersionMetadata? VersionMetadata(this HttpChain chain) =>
        chain.BuildEndpoint(RouteWarmup.Lazy).Metadata.GetMetadata<ApiVersionMetadata>();

    /// <summary>How many <see cref="ApiVersionMetadata"/> instances are attached — used to assert "exactly once".</summary>
    public static int VersionMetadataCount(this HttpChain chain) =>
        chain
            .BuildEndpoint(RouteWarmup.Lazy)
            .Metadata.GetOrderedMetadata<ApiVersionMetadata>()
            .Count;

    /// <summary>
    /// The <see cref="TagsAttribute"/> instances on the built endpoint. A named <c>ApiVersionSet</c>
    /// would inject one (<c>TagsAttribute(setName)</c>); the bridge builds an unnamed set, and Wolverine
    /// adds no <see cref="TagsAttribute"/> of its own, so this must stay empty.
    /// </summary>
    public static IReadOnlyList<TagsAttribute> TagMetadata(this HttpChain chain) =>
        chain.BuildEndpoint(RouteWarmup.Lazy).Metadata.GetOrderedMetadata<TagsAttribute>();

    /// <summary>All metadata of type <typeparamref name="T"/> on the built endpoint.</summary>
    public static IReadOnlyList<T> MetadataOf<T>(this HttpChain chain)
        where T : class => chain.BuildEndpoint(RouteWarmup.Lazy).Metadata.GetOrderedMetadata<T>();

    /// <summary>
    /// The group-wide (set) <see cref="ApiVersionModel"/> — the aggregate version space shared by
    /// every chain in the route group. First component of <see cref="ApiVersionMetadata"/>.
    /// </summary>
    public static ApiVersionModel GroupModel(this HttpChain chain)
    {
        var (api, _) = chain.VersionMetadata()!;
        return api;
    }

    /// <summary>
    /// The per-endpoint <see cref="ApiVersionModel"/> — the versions this specific chain declares,
    /// implements, supports, and deprecates. Second component of <see cref="ApiVersionMetadata"/>.
    /// </summary>
    public static ApiVersionModel EndpointModel(this HttpChain chain)
    {
        var (_, endpoint) = chain.VersionMetadata()!;
        return endpoint;
    }
}
