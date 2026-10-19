using FluentValidation;
using Marten;
using Wolverine.Http;

namespace CleanArchitectureTodos;

public record UpdateTodoItemRequest(string Title, bool Done)
{
    public class Validator : AbstractValidator<UpdateTodoItemRequest>
    {
        public Validator()
        {
            RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        }
    }
}

public static class UpdateTodoItemEndpoint
{
    // Items are nested in TodoList documents — must query by child Guid.
    // IResult justified: need to search across lists for the nested item.
    [WolverinePut("/api/todoitems/{id}")]
    public static async Task<IResult> Put(Guid id, UpdateTodoItemRequest request, IDocumentSession session, CancellationToken ct)
    {
        var list = await session.Query<TodoList>()
            .Where(l => l.Items.Any(i => i.Id == id))
            .FirstOrDefaultAsync(ct);

        if (list is null) return Results.NotFound();

        var item = list.Items.First(i => i.Id == id);
        var wasDone = item.Done;
        item.Title = request.Title;
        item.Done = request.Done;
        list.LastModified = DateTimeOffset.UtcNow;

        session.Store(list);

        if (!wasDone && item.Done)
        {
            Console.WriteLine($"[TodoItemCompleted] Item '{item.Title}' in list '{list.Title}' was marked as done.");
        }

        return Results.NoContent();
    }
}
