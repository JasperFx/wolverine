using System.Diagnostics;
using System.Text.Json.Serialization;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using Wolverine.Http;

namespace WolverineWebApi;

#region sample_http_stopwatch_middleware

public class StopwatchMiddleware
{
    private readonly Stopwatch _stopwatch = new();

    public void Before()
    {
        _stopwatch.Start();
    }

    public void Finally(ILogger logger, HttpContext context)
    {
        _stopwatch.Stop();
        logger.LogDebug("Request for route {Route} ran in {Duration} milliseconds",
            context.Request.Path, _stopwatch.ElapsedMilliseconds);
    }
}

#endregion

#region sample_applying_middleware_programmatically_to_one_chain

public class MeasuredEndpoint
{
    // The signature is meaningful here
    public static void Configure(HttpChain chain)
    {
        // Call this method before the normal endpoint
        chain.Middleware.Add(MethodCall.For<StopwatchMiddleware>(x => x.Before()));

        // Call this method after the normal endpoint
        chain.Postprocessors.Add(MethodCall.For<StopwatchMiddleware>(x => x.Finally(null, null)));
    }

    [WolverineGet("/timed")]
    public async Task<string> Get()
    {
        await Task.Delay(100.Milliseconds());
        return "how long did I take?";
    }
}

#endregion

public class MiddlewareEndpoints
{
    [WolverineGet("/middleware/simple")]
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

    [WolverineGet("/middleware/intrinsic")]
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

#region sample_fake_authentication_middleware

public class FakeAuthenticationMiddleware
{
    public static IResult Before(IAmAuthenticated message)
    {
        return message.Authenticated
            // This tells Wolverine to just keep going
            ? WolverineContinue.Result()

            // If the IResult is not WolverineContinue, Wolverine
            // will execute the IResult and stop processing otherwise
            : Results.Unauthorized();
    }
}

#endregion

public class AuthenticatedRequest : IAmAuthenticated
{
    [JsonPropertyName("authenticated")] public bool Authenticated { get; set; }
}

public class AuthenticatedEndpoint
{
    [WolverinePost("/authenticated")]
    public string Get(AuthenticatedRequest request)
    {
        return "All good.";
    }
}

#region sample_middleware_created_dependency

public class HttpMiddlewareUser
{
    public string Name { get; set; }
}

public class HttpServiceWithMiddlewareUser
{
    public HttpMiddlewareUser User { get; }

    public HttpServiceWithMiddlewareUser(HttpMiddlewareUser user) => User = user;
}

public static class HttpMiddlewareUserCreatingMiddleware
{
    public static HttpMiddlewareUser Before() => new() { Name = "HttpTestUser" };
}

public class MiddlewareServiceDependencyEndpoint
{
    [WolverineGet("/middleware/service-dependency")]
    public string Get(HttpServiceWithMiddlewareUser service, Recorder recorder)
    {
        recorder.Actions.Add($"Handler received Service with User: {service.User.Name}");
        return $"User: {service.User.Name}";
    }
}

#endregion