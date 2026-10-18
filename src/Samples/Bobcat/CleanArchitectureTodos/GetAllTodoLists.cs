using Marten;
using Wolverine.Http;

namespace CleanArchitectureTodos;

public static class GetAllTodoListsEndpoint
{
    [WolverineGet("/api/todolists")]
    public static async Task<TodosVm> Get(IQuerySession session, CancellationToken ct)
    {
        var lists = await session.Query<TodoList>()
            .OrderBy(l => l.Title)
            .ToListAsync(ct);

        var priorityLevels = Enum.GetValues<PriorityLevel>()
            .Select(p => new LookupDto((int)p, p.ToString()))
            .ToList();

        var colours = TodoColours.Supported
            .Select(c => new ColourDto(c.Key, c.Value))
            .ToList();

        var listDtos = lists.Select(l => new TodoListDto(
            l.Id,
            l.Title,
            l.Colour,
            l.Items.Select(i => new TodoItemDto(i.Id, i.Title, i.Note, (int)i.Priority, i.Done)).ToList()
        )).ToList();

        return new TodosVm(priorityLevels, colours, listDtos);
    }
}
