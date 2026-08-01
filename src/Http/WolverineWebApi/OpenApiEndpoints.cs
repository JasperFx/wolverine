using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Wolverine;
using Wolverine.Http;
using WolverineWebApi.TestSupport;

namespace WolverineWebApi;

public class OpenApiEndpoints
{
    /* Test Cases
     1. EmptyResponse returning an event in AggregateHandler, 204, no 200, no body
     2. Return NoContent, 204, no 200, no body
     3. Return NoContent first in tuple, 204, no 200, no body
     4. [EmptyResponse] override, verify no 200, but a 204, no body
     
     
     */

    [ExpectMatch(OperationType.Get, "/expect/string")]
    [WolverineGet("/openapi/string")]
    public static string GetString()
    {
        return "hello";
    }

    [ExpectStatusCodes(200, 404)]
    [ExpectProduces(200, typeof(Reservation), "application/json")]
    [WolverineGet("/openapi/json")]
    public Reservation GetJson()
    {
        return new Reservation();
    }

    [ExpectMatch(OperationType.Get, "/expect/getresult")]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(string), 200)]
    [WolverineGet("/openapi/getresult")]
    public IResult GetResult()
    {
        return Results.Ok("a string");
    }

    [ExpectMatch(OperationType.Get, "/expect/getresulttyped")]
    [WolverineGet("/openapi/getresulttyped")]
    public Results<Ok<Reservation>, ProblemHttpResult> GetResultTyped()
    {
        return TypedResults.Ok(new Reservation());
    }

    [ExpectMatch(OperationType.Get, "/expect/nocontent")]
    [WolverineGet("/openapi/nocontent")]
    public NoContent Empty() => TypedResults.NoContent();

    [ExpectMatch(OperationType.Get, "/expect/nocontent")]
    [WolverineGet("/openapi/sideeffect")]
    public static SimpleSideEffect SideEffect() => new SimpleSideEffect();

    [ExpectMatch(OperationType.Post, "/expect/nocontent")]
    [WolverinePost("/openapi/empty"), EmptyResponse]
    public HttpMessage1 PostCommand()
    {
        return new HttpMessage1("foo");
    }

    public static void BuildComparisonRoutes(WebApplication app)
    {
        app.MapGet("/expect/nocontent", () => TypedResults.NoContent());
        app.MapPost("/expect/nocontent", () => TypedResults.NoContent());
        app.MapGet("/expect/string", () => "hello, world");
        app.MapGet("/expect/json", () => TypedResults.Json(new Reservation())).Produces(404);
        app.MapGet("/expect/getresult",  () => Results.Ok("a string")).ProducesProblem(400).Produces(404).Produces<string>(contentType: "text/plain");
        app.MapGet("/expect/getresulttyped", Results<Ok<Reservation>, ProblemHttpResult> () => TypedResults.Ok(new Reservation()));
    }
}

public class SimpleSideEffect : ISideEffect
{
    public void Execute()
    {
        Debug.WriteLine("All good");
    }
}