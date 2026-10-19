using Marten;
using Wolverine.Http;

namespace CleanArchitectureTodos;

public record UpdateTodoItemDetailRequest(int ListId, PriorityLevel Priority, string? Note);

public static class UpdateTodoItemDetailEndpoint
{
    // IResult justified: may move item between lists (variable logic paths)
    [WolverinePatch("/api/todoitems/detail/{id}")]
    public static async Task<IResult> Patch(Guid id, UpdateTodoItemDetailRequest request, IDocumentSession session, CancellationToken ct)
    {
        var currentList = await session.Query<TodoList>()
            .Where(l => l.Items.Any(i => i.Id == id))
            .FirstOrDefaultAsync(ct);

        if (currentList is null) return Results.NotFound();

        var item = currentList.Items.First(i => i.Id == id);
        item.Priority = request.Priority;
        item.Note = request.Note;

        if (request.ListId != currentList.Id)
        {
            var targetList = await session.LoadAsync<TodoList>(request.ListId, ct);
            if (targetList is null) return Results.NotFound();

            currentList.Items.Remove(item);
            targetList.Items.Add(item);
            currentList.LastModified = DateTimeOffset.UtcNow;
            targetList.LastModified = DateTimeOffset.UtcNow;

            session.Store(currentList);
            session.Store(targetList);
        }
        else
        {
            currentList.LastModified = DateTimeOffset.UtcNow;
            session.Store(currentList);
        }

        return Results.NoContent();
    }
}
