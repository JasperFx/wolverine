using Microsoft.AspNetCore.RateLimiting;
using Wolverine.Http;

namespace WolverineWebApi.RateLimiting;

public static class RateLimitedEndpoints
{
    #region sample_rate_limited_endpoint
    [WolverineGet("/api/rate-limited")]
    [EnableRateLimiting("fixed")]
    public static string GetRateLimited()
    {
        return "OK";
    }
    #endregion

    [WolverineGet("/api/not-rate-limited")]
    public static string GetNotRateLimited()
    {
        return "OK";
    }
}
