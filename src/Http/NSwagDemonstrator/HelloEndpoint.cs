using Wolverine.Http;

namespace NSwagDemonstrator;

public class HelloEndpoint
{
    [WolverineGet("/")]
    public string Get() => "Hello.";
}
