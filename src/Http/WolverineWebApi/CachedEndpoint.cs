using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public static class CachedEndpoint
{
    #region sample_using_response_cache_attribute

    // This is all it takes:
    [WolverineGet("/cache/one"), ResponseCache(Duration = 3, VaryByHeader = "accept-encoding", NoStore = false)]
    public static string GetOne()
    {
        return "one";
    }

    [WolverineGet("/cache/two"), ResponseCache(Duration = 10, NoStore = true)]
    public static string GetTwo()
    {
        return "two";
    }

    #endregion

    [WolverineGet("/cache/none")]
    public static string GetNone()
    {
        return "none";
    }
}