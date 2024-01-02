using Wolverine.Http;

namespace WolverineWebApi;

public class IgnoredEndpoint
{
    [WolverineGet(("/ignore")), ExcludeFromDescription]
    public string Ignore()
    {
        return "nothing";
    }
}