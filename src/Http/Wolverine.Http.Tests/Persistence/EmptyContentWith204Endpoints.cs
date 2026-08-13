using Wolverine.Persistence;
using WolverineWebApi.Todos;

namespace Wolverine.Http.Tests.Persistence;

/// <summary>
///     Endpoints for the two independent "there is nothing to send back" paths:
///     <see cref="OnMissing.EmptyContentWith204" /> covers a required entity that could not be loaded, while
///     <see cref="NoContentIfMissingAttribute" /> covers an endpoint whose response body is simply null.
/// </summary>
// Deliberately not a static class -- HttpChain.ChainFor<T>() needs a usable type argument
public class EmptyContentWith204Endpoints
{
    // The entity guard answers 204 instead of the default 404
    [WolverineGet("/no-content/entity/{id}")]
    public static Todo2 GetEntity([Entity(OnMissing = OnMissing.EmptyContentWith204)] Todo2 todo) => todo;

    // Required = false is deliberately ignored here: on a GET, EmptyContentWith204 forces the entity to be
    // treated as required, because running the endpoint with a null entity to return an empty body anyway
    // buys nothing. Without that, this endpoint would NRE on todo.Name.
    [WolverineGet("/no-content/entity-not-required/{id}")]
    public static string GetEntityNotRequired(
        [Entity(OnMissing = OnMissing.EmptyContentWith204, Required = false)] Todo2 todo) => todo.Name!;

    // No entity attribute at all -- just an endpoint whose resource comes back null
    [WolverineGet("/no-content/body/{id}"), NoContentIfMissing]
    public static Todo2? GetBody(string id) => id == "found" ? new Todo2 { Id = id, Name = "Found" } : null;

    // The unannotated control for GetBody
    [WolverineGet("/no-content/body-default/{id}")]
    public static Todo2? GetBodyDefault(string id) => id == "found" ? new Todo2 { Id = id, Name = "Found" } : null;

    // Same, but the resource type is a string, which goes through a different response writer
    [WolverineGet("/no-content/string/{id}"), NoContentIfMissing]
    public static string? GetString(string id) => id == "found" ? "Found" : null;

    // The unannotated control for the string writer: a null string used to throw a NullReferenceException
    // and answer 500 rather than the 404 every other resource type answered
    [WolverineGet("/no-content/string-default/{id}")]
    public static string? GetStringDefault(string id) => id == "found" ? "Found" : null;
}

/// <summary>
///     A class level [NoContentIfMissing] applies to every endpoint method in the class.
/// </summary>
[NoContentIfMissing]
public static class ClassLevelNoContentEndpoints
{
    [WolverineGet("/no-content/class-level/{id}")]
    public static Todo2? Get(string id) => id == "found" ? new Todo2 { Id = id, Name = "Found" } : null;

    // ... unless the method opts back out
    [WolverineGet("/no-content/class-level-opt-out/{id}"), NotFoundIfMissing]
    public static Todo2? GetOptOut(string id) => id == "found" ? new Todo2 { Id = id, Name = "Found" } : null;
}

/// <summary>
///     Endpoints used to prove the global <c>WolverineHttpOptions.OnMissingResponseBody</c> setting, which only
///     reaches GET and QUERY endpoints.
/// </summary>
public static class GlobalMissingResponseBodyEndpoints
{
    [WolverineGet("/global-no-content/get/{id}")]
    public static Todo2? Get(string id) => id == "found" ? new Todo2 { Id = id, Name = "Found" } : null;

    [WolverineGet("/global-no-content/opt-out/{id}"), NotFoundIfMissing]
    public static Todo2? GetOptOut(string id) => id == "found" ? new Todo2 { Id = id, Name = "Found" } : null;

    [WolverinePost("/global-no-content/post")]
    public static Todo2? Post(CreateTodo2 command) => command.Id == "found" ? new Todo2 { Id = command.Id } : null;
}

/// <summary>
///     Uses a plain [Entity], so it picks up whatever <c>WolverineOptions.EntityDefaults.OnMissing</c> is set to.
/// </summary>
public static class GlobalEmptyContentEntityEndpoint
{
    [WolverineGet("/global-no-content/entity/{id}")]
    public static Todo2 Get([Entity] Todo2 todo) => todo;
}
