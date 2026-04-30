using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

/// <summary>Fluent helpers for declaring API versioning behavior on individual <see cref="HttpChain"/> instances inside <c>ConfigureEndpoints</c>.</summary>
public static class HttpChainApiVersioningExtensions
{
    /// <summary>Mark this chain as deprecated by attaching a default <see cref="DeprecationPolicy"/> with no scheduled date or links.</summary>
    public static HttpChain MarkDeprecated(this HttpChain chain)
    {
        chain.DeprecationPolicy ??= new DeprecationPolicy();
        return chain;
    }
}
