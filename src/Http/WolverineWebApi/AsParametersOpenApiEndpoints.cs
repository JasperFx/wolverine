using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Wolverine.Http;
using WolverineWebApi.TestSupport;

namespace WolverineWebApi;

// GH-3135 OpenAPI-shape coverage for [AsParameters] and overlapping route/body parameters.
// These endpoints are deliberately free of database/Marten dependencies so they participate in the
// Swashbuckle-driven open_api_generation harness. The new Expect* attributes assert parameter
// type/format and request-body shape (presence AND absence of properties).

public record AddPassengerPayload(string PassengerName);

// uniquelau's preferred shape (issue CASE B): [AsParameters] splitting a typed [FromRoute] id from a
// [FromBody] payload. The route id must keep its real type/format (uuid) and the body must be just
// the payload member type — the container and the route-bound id must NOT appear in the body.
public record AddPassengerCommand([FromRoute] Guid JourneyId, [FromBody] AddPassengerPayload Body);

public static class AsParametersSplitEndpoint
{
    [WolverinePost("/api/3135/journey/{journeyId:guid}/passenger")]
    [ExpectParameter("journeyId", ParameterLocation.Path, Type = "string", Format = "uuid")]
    // Non-nullable [FromBody] member -> required body.
    [ExpectRequestBody("application/json", "passengerName", ForbiddenProperties = ["journeyId", "body"], Required = true)]
    public static string Post([AsParameters] AddPassengerCommand command)
        => $"{command.JourneyId}:{command.Body.PassengerName}";
}

// GH-3135 WS2/WS3: a nullable [FromBody] member is an OPTIONAL body — requestBody.required is false
// and an empty request body binds null at runtime (200) instead of 400.
public class OptionalBodyQuery
{
    [FromQuery]
    public string? Name { get; set; }

    [FromBody]
    public AddPassengerPayload? Body { get; set; }
}

public static class OptionalBodyEndpoint
{
    [WolverinePost("/api/3135/optional-body")]
    [ExpectParameter("Name", ParameterLocation.Query, Type = "string")]
    [ExpectRequestBody("application/json", "passengerName", Required = false)]
    public static string Post([AsParameters] OptionalBodyQuery query)
        => query.Body is null ? "no-body" : $"body:{query.Body.PassengerName}";
}

// uniquelau's "Alternative" workaround (issue CASE A): a plain complex body whose property overlaps a
// route token. The duplication (journeyId in path AND body) is intentional ASP.NET-parity behavior
// and is locked in here; the win from GH-3135 is that the path param now carries format: uuid.
public record AddPassengerFlatCommand(Guid JourneyId, string PassengerName);

public static class PlainBodyRouteOverlapEndpoint
{
    [WolverinePost("/api/3135/flat/journey/{journeyId:guid}/passenger")]
    [ExpectParameter("journeyId", ParameterLocation.Path, Type = "string", Format = "uuid")]
    [ExpectRequestBody("application/json", "journeyId", "passengerName")]
    public static string Post(AddPassengerFlatCommand command)
        => $"{command.JourneyId}:{command.PassengerName}";
}

// Isolates the case-insensitive route-variable match (GH-3135): the member is PascalCase `Number`
// while the route token is lowercase `number`, and the route is UNCONSTRAINED — so only a
// case-insensitive match keeps the parameter typed as integer rather than falling back to string.
public record RouteIntQuery([FromRoute] int Number, [FromQuery] string? Name);

public static class AsParametersRouteIntEndpoint
{
    [WolverinePost("/api/3135/route-int/{number}")]
    [ExpectParameter("number", ParameterLocation.Path, Type = "integer")]
    [ExpectParameter("Name", ParameterLocation.Query, Type = "string")]
    [ExpectNoRequestBody]
    public static string Post([AsParameters] RouteIntQuery query)
        => $"{query.Number}:{query.Name}";
}

// Exercises the route-constraint -> schema type/format fallback (GH-3135) when no typed argument is
// bound to the route value at all (plain string handler argument, constrained route tokens).
public static class RouteConstraintTypingEndpoint
{
    [WolverineGet("/api/3135/constraints/{id:guid}/{count:int}")]
    [ExpectParameter("id", ParameterLocation.Path, Type = "string", Format = "uuid")]
    [ExpectParameter("count", ParameterLocation.Path, Type = "integer")]
    [ExpectNoRequestBody]
    public static string Get(Guid id, int count) => $"{id}:{count}";
}
