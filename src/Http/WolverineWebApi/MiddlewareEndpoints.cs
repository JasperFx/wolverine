using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public class MiddlewareEndpoints
{
    [HttpGet("/middleware/simple")]
    public string GetRequest(Recorder recorder)
    {
        recorder.Actions.Add("Action");
        return "okay";
    }
}


public static class BeforeAndAfterMiddleware
{
    public static void Before(Recorder recorder)
    {
        recorder.Actions.Add("Before");
    }
    
    public static void After(Recorder recorder)
    {
        recorder.Actions.Add("After");
    }
}

public class BeforeAndAfterEndpoint
{
    public static void Before(Recorder recorder)
    {
        recorder.Actions.Add("Before");
    }
    
    public static void After(Recorder recorder)
    {
        recorder.Actions.Add("After");
    }
    
    [HttpGet("/middleware/intrinsic")]
    public string GetRequest(Recorder recorder)
    {
        recorder.Actions.Add("Action");
        return "okay";
    }
}

public interface IAmAuthenticated
{
    bool Authenticated { get; set; }
}

public class FakeAuthenticationMiddleware
{
    public static IResult Before(IAmAuthenticated message)
    {
        return message.Authenticated ? WolverineContinue.Result() : Microsoft.AspNetCore.Http.Results.Unauthorized();
    }
}

public class AuthenticatedRequest : IAmAuthenticated
{
    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; set; }
}

public class AuthenticatedEndpoint
{
    [HttpPost("/authenticated")]
    public string Get(AuthenticatedRequest request) => "All good.";
}