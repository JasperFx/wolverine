using Marten;
using Wolverine;
using Wolverine.Http;

namespace MultiTenantedTodoWebService;



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
    [WolverineGet("/todoitems/{tenant}")]
    public static Task<IReadOnlyList<Todo>> Get(string tenant, IDocumentStore store)
    {
        using var session = store.QuerySession(tenant);
        return session.Query<Todo>().ToListAsync();
    }

    [WolverineGet("/todoitems/{tenant}/complete")]
    public static Task<IReadOnlyList<Todo>> GetComplete(string tenant, IDocumentStore store)
    {
        using var session = store.QuerySession(tenant);
        return session.Query<Todo>().Where(x => x.IsComplete).ToListAsync();
    }

    // Wolverine can infer the 200/404 status codes for you here
    // so there's no code noise just to satisfy OpenAPI tooling
    [WolverineGet("/todoitems/{tenant}/{id}")]
    public static async Task<Todo?> GetTodo(int id, string tenant, IDocumentStore store, CancellationToken cancellation)
    {
        using var session = store.QuerySession(tenant);
        var todo = await session.LoadAsync<Todo>(id, cancellation);

        return todo;
    }

    #region sample_calling_invoke_for_tenant_async_with_expected_result

    [WolverinePost("/todoitems/{tenant}")]
    public static async Task<IResult> Create(string tenant, CreateTodo command, IMessageBus bus)
    {
        // At the 1.0 release, you would have to use Wolverine as a mediator
        // to get the full multi-tenancy feature set.
        
        // That hopefully changes in 1.1
        var created = await bus.InvokeForTenantAsync<TodoCreated>(tenant, command);

        return Results.Created($"/todoitems/{tenant}/{created.Id}", created);
    }

    #endregion

    #region sample_invoke_for_tenant

    [WolverineDelete("/todoitems/{tenant}")]
    public static async Task Delete(
        string tenant, 
        DeleteTodo command, 
        IMessageBus bus)
    {
        // Invoke inline for the specified tenant
        await bus.InvokeForTenantAsync(tenant, command);
    }

    #endregion
}


public static class TodoCreatedHandler
{
    public static void Handle(DeleteTodo command, IDocumentSession session)
    {
        session.Delete<Todo>(command.Id);
    }
    
    public static TodoCreated Handle(CreateTodo command, IDocumentSession session)
    {
        var todo = new Todo { Name = command.Name };
        session.Store(todo);

        return new TodoCreated(todo.Id);
    }
    
    // Do something in the background, like assign it to someone,
    // send out emails or texts, alerts, whatever
    public static void Handle(TodoCreated created, ILogger logger)
    {
        logger.LogInformation("Got a new TodoCreated event for " + created.Id);
    }    
}


