using Marten;
using Wolverine;
using Wolverine.Http;

namespace TodoWebService;

#region sample_Todo

public class Todo
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool IsComplete { get; set; }
}

#endregion

public record CreateTodo(string Name);

public record UpdateTodo(int Id, string Name, bool IsComplete);

public record DeleteTodo(int Id);

public record TodoCreated(int Id);

public static class TodoEndpoints
{
    #region sample_get_to_json

    [WolverineGet("/todoitems")]
    public static Task<IReadOnlyList<Todo>> Get(IQuerySession session)
        => session.Query<Todo>().ToListAsync();

    #endregion

    [WolverineGet("/todoitems/complete")]
    public static Task<IReadOnlyList<Todo>> GetComplete(IQuerySession session) =>
        session.Query<Todo>().Where(x => x.IsComplete).ToListAsync();

    #region sample_GetTodo

    // Wolverine can infer the 200/404 status codes for you here
    // so there's no code noise just to satisfy OpenAPI tooling
    [WolverineGet("/todoitems/{id}")]
    public static Task<Todo?> GetTodo(int id, IQuerySession session, CancellationToken cancellation)
        => session.LoadAsync<Todo>(id, cancellation);

    #endregion

    #region sample_posting_new_todo_with_middleware

    [WolverinePost("/todoitems")]
    public static async Task<IResult> Create(CreateTodo command, IDocumentSession session, IMessageBus bus)
    {
        var todo = new Todo { Name = command.Name };
        session.Store(todo);

        // Going to raise an event within out system to be processed later
        await bus.PublishAsync(new TodoCreated(todo.Id));

        return Results.Created($"/todoitems/{todo.Id}", todo);
    }

    #endregion

    [WolverineDelete("/todoitems")]
    public static void Delete(DeleteTodo command, IDocumentSession session)
        => session.Delete<Todo>(command.Id);
}

public static class TodoCreatedHandler
{
    // Do something in the background, like assign it to someone,
    // send out emails or texts, alerts, whatever
    public static void Handle(TodoCreated created, ILogger logger)
    {
        logger.LogInformation("Got a new TodoCreated event for " + created.Id);
    }
}

#region sample_UpdateTodoEndpoint

public static class UpdateTodoEndpoint
{
    public static async Task<(Todo? todo, IResult result)> LoadAsync(UpdateTodo command, IDocumentSession session)
    {
        var todo = await session.LoadAsync<Todo>(command.Id);
        return todo != null
            ? (todo, new WolverineContinue())
            : (todo, Results.NotFound());
    }

    [WolverinePut("/todoitems")]
    public static void Put(UpdateTodo command, Todo todo, IDocumentSession session)
    {
        todo.Name = todo.Name;
        todo.IsComplete = todo.IsComplete;
        session.Store(todo);
    }
}

#endregion

