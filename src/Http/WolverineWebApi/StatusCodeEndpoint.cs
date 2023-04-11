using Wolverine.Http;

namespace WolverineWebApi;

public class StatusCodeEndpoint
{
    [WolverinePost("/status")]
    public static int PostStatusCode(StatusCodeRequest request, ILogger logger)
    {
        logger.LogDebug("Here.");
        return request.StatusCode;
    }
}

public record StatusCodeRequest(int StatusCode);