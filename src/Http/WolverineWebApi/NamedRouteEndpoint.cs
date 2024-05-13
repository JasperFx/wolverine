using Wolverine.Http;

namespace WolverineWebApi;

public class NamedRouteEndpoint
{
    [WolverinePost("/named/route", "NamedRoute")]
    public string Post()
    {
        return "Hello";
    } 
}