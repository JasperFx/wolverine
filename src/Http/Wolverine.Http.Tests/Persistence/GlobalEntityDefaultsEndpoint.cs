using Wolverine.Persistence;
using WolverineWebApi.Todos;

namespace Wolverine.Http.Tests.Persistence;

public static class GlobalEntityDefaultsEndpoint
{
    // Uses plain [Entity] - should pick up global default
    [WolverineGet("/global-defaults/todo/{id}")]
    public static Todo2 GetDefault([Entity] Todo2 todo) => todo;

    // Explicit override to Simple404 - should override global default
    [WolverineGet("/global-defaults/todo-simple/{id}")]
    public static Todo2 GetSimple([Entity(OnMissing = OnMissing.Simple404)] Todo2 todo) => todo;
}
