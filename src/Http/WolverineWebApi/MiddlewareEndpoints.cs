using System.Diagnostics;
using System.Text.Json.Serialization;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using Microsoft.AspNetCore.Mvc;
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
        chain.Postprocessors.Add(MethodCall.For<StopwatchMiddleware>(x => x.Finally(null!, null!)));
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

// GH-4308: a postprocessor parameter whose name collides with a route segment on a route-bindable
// type is claimed by the route — OpenAPI renders only the single Path parameter (GH-3601), and the
// generated code must make the same claim by binding the parameter from the parsed route value. Before
// the fix this shape didn't compile at all: nothing produced a `long` variable for After's parameter
// and codegen died with an UnResolvableVariableException (which is what failed `codegen preview` on
// WolverineWebApi, so this endpoint also keeps the Nuke CodegenPreviewCommand guarding the shape).
public static class PostprocessorRouteCollisionRecordingEndpoint
{
    [WolverineGet("/middleware/postprocessor-route/{orderId:long}")]
    public static string Get() => "ok";

    public static void After(Recorder recorder, [FromQuery] long orderId)
    {
        recorder.Actions.Add($"After: {orderId}");
    }
}

// GH-4314: a postprocessor [FromQuery]/[FromHeader] parameter that does NOT collide with any route
// segment must bind from the query string / header, exactly as the OpenAPI description has claimed
// since GH-3601. Before the fix nothing produced a variable for these parameters at all, and
// JasperFx's name-then-type fallback silently handed `audit` the endpoint's response body — the only
// other string in the chain — so user code documented to receive a query value received the response
// instead. The `string` response return is deliberate bait for that fallback.
public static class PostprocessorQueryHeaderRecordingEndpoint
{
    [WolverineGet("/middleware/postprocessor-query-header")]
    public static string Get() => "ok";

    public static void After(Recorder recorder, [FromQuery] string? audit, [FromQuery] int attempts,
        [FromHeader(Name = "x-trace")] string? trace)
    {
        recorder.Actions.Add($"After: audit={audit ?? "null"}, attempts={attempts}, trace={trace ?? "null"}");
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

// GH-3892: one middleware class that declares BOTH a short-circuiting Before
// (return type includes IResult) and a Finally used to blow up HTTP chain
// code generation with a NullReferenceException
public class ShortCircuitBeforeWithFinallyMiddleware
{
    public static Task<IResult> Before(HttpContext httpContext, Recorder recorder)
    {
        recorder.Actions.Add("ShortCircuit.Before");
        return httpContext.Request.Query.ContainsKey("stop")
            ? Task.FromResult(Results.StatusCode(418))
            : Task.FromResult<IResult>(WolverineContinue.Result());
    }

    public static Task Finally(HttpContext httpContext, Recorder recorder)
    {
        recorder.Actions.Add("ShortCircuit.Finally");
        return Task.CompletedTask;
    }
}

public class MiddlewareReservation
{
    public string Value { get; set; } = "reserved";
}

// GH-3892 variant: instance middleware, synchronous Before returning a tuple
// that carries the IResult, paired with FinallyAsync
public class TupleShortCircuitFinallyAsyncMiddleware
{
    public (IResult, MiddlewareReservation) Before(HttpContext httpContext, Recorder recorder)
    {
        recorder.Actions.Add("TupleShortCircuit.Before");
        var reservation = new MiddlewareReservation();
        return httpContext.Request.Query.ContainsKey("stop")
            ? (Results.StatusCode(419), reservation)
            : (WolverineContinue.Result(), reservation);
    }

    public Task FinallyAsync(MiddlewareReservation reservation, Recorder recorder)
    {
        recorder.Actions.Add($"TupleShortCircuit.FinallyAsync:{reservation.Value}");
        return Task.CompletedTask;
    }
}

public class ShortCircuitFinallyEndpoints
{
    [Wolverine.Attributes.Middleware(typeof(ShortCircuitBeforeWithFinallyMiddleware))]
    [WolverineGet("/middleware/shortcircuit-finally")]
    public string Get(Recorder recorder)
    {
        recorder.Actions.Add("ShortCircuit.Action");
        return "ok";
    }

    [Wolverine.Attributes.Middleware(typeof(ShortCircuitBeforeWithFinallyMiddleware))]
    [WolverineGet("/middleware/shortcircuit-finally/throws")]
    public string GetThrows(Recorder recorder)
    {
        recorder.Actions.Add("ShortCircuit.Throws");
        throw new DivideByZeroException("boom");
    }

    [Wolverine.Attributes.Middleware(typeof(TupleShortCircuitFinallyAsyncMiddleware))]
    [WolverineGet("/middleware/shortcircuit-finally/tuple")]
    public string GetTuple(Recorder recorder)
    {
        recorder.Actions.Add("TupleShortCircuit.Action");
        return "ok";
    }
}