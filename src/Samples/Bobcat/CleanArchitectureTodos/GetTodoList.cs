using Wolverine.Http;
using Wolverine.Persistence;

namespace CleanArchitectureTodos;

public static class GetTodoListEndpoint
{
    // The one read endpoint this sample was missing. Without it a list could only be written
    // to or found in the unsorted bulk of GET /api/todolists, so the specs' central claims —
    // a new list gets the default colour, an item update actually changed the stored item —
    // were unobservable. [Entity] answers 404 for an id that does not exist.
    // See docs/sample-wiring.md footgun 9.
    [WolverineGet("/api/todolists/{id}")]
    public static TodoList Get([Entity(Required = true)] TodoList list) => list;
}
