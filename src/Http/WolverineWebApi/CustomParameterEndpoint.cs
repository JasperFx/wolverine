using Wolverine.Http;

namespace WolverineWebApi;

public class CustomParameterEndpoint
{
    #region sample_http_endpoint_receiving_now

    [WolverineGet("/now")]
    public static string GetNow(DateTimeOffset now) // using the custom parameter strategy for "now"
    {
        return now.ToString();
    }

    #endregion
}