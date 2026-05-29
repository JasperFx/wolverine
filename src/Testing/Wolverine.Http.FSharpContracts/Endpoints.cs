using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.FSharpContracts;

/// <summary>The JSON body bound by <see cref="ThingEndpoints.Create" />.</summary>
public record CreateThing(string Name);

/// <summary>The JSON result returned by <see cref="ThingEndpoints.Create" />.</summary>
public record ThingCreated(string Name);

/// <summary>
///     The "smallest viable" Wolverine.Http endpoints for the F# code-generation audit
///     (issue GH-2969, Phase C): a GET returning a string and a POST binding a JSON body and
///     returning a JSON result. The driver renders each endpoint's real <c>HttpChain</c> to F#, and
///     the fixture compiles the generated <c>HttpHandler</c> adapters against these public types.
/// </summary>
public class ThingEndpoints
{
    [WolverineGet("/fsharp/hello")]
    public string Hello()
    {
        return "hello from F#";
    }

    [WolverinePost("/fsharp/things")]
    public ThingCreated Create(CreateThing command)
    {
        return new ThingCreated(command.Name);
    }

    // Route-value binding: {id} is bound from the route. {count} is a typed (int) route value.
    [WolverineGet("/fsharp/things/{id}")]
    public string GetById(string id)
    {
        return $"thing {id}";
    }

    [WolverineGet("/fsharp/things/{id}/items/{count}")]
    public string GetItems(string id, int count)
    {
        return $"{count} items for {id}";
    }

    // Query-string binding: q has no matching route token, so it binds from the query string.
    [WolverineGet("/fsharp/search")]
    public string Search(string q)
    {
        return $"searching {q}";
    }

    // Typed query-string binding: page is an int bound from the query string (default when absent/unparseable).
    [WolverineGet("/fsharp/paged")]
    public string Paged(int page)
    {
        return $"page {page}";
    }

    // IResult return: a terminal IResult endpoint. The handler's IResult is executed directly as the
    // returned Task (ReturnFromLastNode); combined with a route value it exercises the AsyncMode-aware
    // abort (a missing route value yields Task.CompletedTask, not unit).
    [WolverineGet("/fsharp/result/{id}")]
    public IResult GetResult(string id)
    {
        return string.IsNullOrEmpty(id) ? Results.NotFound() : Results.Ok($"thing {id}");
    }
}
