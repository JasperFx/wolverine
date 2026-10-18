using Marten;
using Wolverine.Http;

namespace CleanArchitectureTodos;

public static class DeleteTodoItemEndpoint
{
    // Items are nested — must query parent list by child Guid
    [WolverineDelete("/api/todoitems/{id}")]
    public static async Task<IResult> Delete(Guid id, IDocumentSession session, CancellationToken ct)
    {
        var list = await session.Query<TodoList>()
            .Where(l => l.Items.Any(i => i.Id == id))
            .FirstOrDefaultAsync(ct);

        if (list is null) return Results.NotFound();

        list.Items.Remove(list.Items.First(i => i.Id == id));
        list.LastModified = DateTimeOffset.UtcNow;

        session.Store(list);

        return Results.NoContent();
    }
}
