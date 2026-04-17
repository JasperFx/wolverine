using Microsoft.AspNetCore.OutputCaching;
using Wolverine.Http;

namespace WolverineWebApi.Caching;

public static class OutputCacheEndpoints
{
    private static int _counter;

    #region sample_output_cache_endpoint
    [WolverineGet("/api/cached")]
    [OutputCache(PolicyName = "short")]
    public static string GetCached()
    {
        return $"Cached at {DateTime.UtcNow:O} - {Interlocked.Increment(ref _counter)}";
    }

    #endregion

    #region sample_output_cache_default_endpoint
    [WolverineGet("/api/cached-default")]
    [OutputCache]
    public static string GetCachedDefault()
    {
        return $"Default cached at {DateTime.UtcNow:O} - {Interlocked.Increment(ref _counter)}";
    }

    #endregion

    [WolverineGet("/api/not-cached")]
    public static string GetNotCached()
    {
        return $"Not cached - {Guid.NewGuid()}";
    }
}
