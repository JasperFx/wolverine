using System.Text.Json.Serialization;
using Wolverine.Http;

namespace WolverineWebApi;

// GH-516 brought to HTTP: middleware that returns the request type replaces the
// (possibly immutable record) request body for the rest of the chain

#region sample_replacing_an_immutable_request_from_http_middleware

public record StampedRequest
{
    public string Name { get; init; } = string.Empty;

    // Server-stamped by middleware, never accepted from the client
    [JsonIgnore]
    public string StampedBy { get; init; } = string.Empty;

    [JsonIgnore]
    public bool Enriched { get; init; }
}

public static class StampedRequestEndpoint
{
    // Implied middleware on the endpoint class itself. Because the methods accept
    // the request type *and* return it, the returned value replaces the request
    // body for the rest of the chain
    public static StampedRequest Before(StampedRequest request)
    {
        return request with { StampedBy = "sync" };
    }

    public static Task<StampedRequest> BeforeAsync(StampedRequest request)
    {
        return Task.FromResult(request with { Name = request.Name + "-async" });
    }

    // The replaced request can also ride along in a tuple with a short-circuiting IResult
    public static (StampedRequest, IResult) Before(StampedRequest request, HttpContext context)
    {
        return request.Name.StartsWith("stop")
            ? (request, Results.StatusCode(423))
            : (request with { Enriched = true }, WolverineContinue.Result());
    }

    [WolverinePost("/middleware/stamped")]
    public static string Handle(StampedRequest request)
    {
        return $"{request.Name}:{request.StampedBy}:{request.Enriched}";
    }
}

#endregion
