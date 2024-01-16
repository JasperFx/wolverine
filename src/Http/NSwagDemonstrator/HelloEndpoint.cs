using Wolverine.Http;

namespace NSwagDemonstrator;

#region sample_hello_world_with_wolverine_http

public class HelloEndpoint
{
    [WolverineGet("/")]
    public string Get() => "Hello.";
}

#endregion