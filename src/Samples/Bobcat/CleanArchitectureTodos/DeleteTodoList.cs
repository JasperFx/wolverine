using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace CleanArchitectureTodos;

public static class DeleteTodoListEndpoint
{
    [WolverineDelete("/api/todolists/{id}")]
    public static void Delete(int id, [Entity(Required = true)] TodoList list, IDocumentSession session)
    {
        session.Delete(list);
    }
}
