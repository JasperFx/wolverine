using Wolverine.Http;

namespace WolverineWebApi;

public class NamedRouteEndpoint
{
    #region sample_using_route_name

    [WolverinePost("/named/route", RouteName = "NamedRoute")]
    public string Post()
    {
        return "Hello";
    } 

    #endregion
}