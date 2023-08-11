using Wolverine.Http;

namespace WolverineWebApi;

public class DiscoverMe
{
    [WolverineGet("/discovered")]
    public static string Get()
    {
        return "You found me!";
    }
}