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
}
