using Microsoft.AspNetCore.Mvc;

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