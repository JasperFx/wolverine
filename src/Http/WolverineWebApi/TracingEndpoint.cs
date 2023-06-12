using Wolverine;
using Wolverine.Http;

namespace WolverineWebApi;

public class TracingEndpoint
{
    [WolverineGet("/correlation")]
    public static string GetCorrelation(IMessageContext context)
    {
        return context.CorrelationId ?? "NONE";
    }
}