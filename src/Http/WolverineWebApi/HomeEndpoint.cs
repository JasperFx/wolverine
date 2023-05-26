using Wolverine.Http;

namespace WolverineWebApi;

public class HomeEndpoint
{
    [WolverineGet("/")]
    public IResult Index()
    {
        return Microsoft.AspNetCore.Http.Results.Redirect("/swagger");
    }
}