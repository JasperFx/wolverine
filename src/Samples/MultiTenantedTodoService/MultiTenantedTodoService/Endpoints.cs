using JasperFx.Core;
using Marten;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

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
    #region sample_get_all_todos

    // The "tenant" route argument would be the route
    [WolverineGet("/todoitems/{tenant}")]
    public static Task<IReadOnlyList<Todo>> Get(string tenant, IQuerySession session)
    {
        return session.Query<Todo>().ToListAsync();
    }

    #endregion

    [WolverineGet("/todoitems/{tenant}/complete")]
    public static Task<IReadOnlyList<Todo>> GetComplete(IQuerySession session) 
        => session
            .Query<Todo>()
            .Where(x => x.IsComplete)
            .ToListAsync();

    // Wolverine can infer the 200/404 status codes for you here
    // so there's no code noise just to satisfy OpenAPI tooling
    [WolverineGet("/todoitems/{tenant}/{id}")]
    public static Task<Todo?> GetTodo(int id, string tenant, IQuerySession session, CancellationToken cancellation)
    {
        return session.LoadAsync<Todo>(id, cancellation);
    }

    #region sample_calling_invoke_for_tenant_async_with_expected_result

    [WolverinePost("/todoitems/{tenant}")]
    public static CreationResponse<TodoCreated> Create(
        // Only need this to express the location of the newly created
        // Todo object
        string tenant, 
        CreateTodo command, 
        IDocumentSession session)
    {
        var todo = new Todo { Name = command.Name };
        
        // Marten itself sets the Todo.Id identity
        // in this call
        session.Store(todo); 

        // New syntax in Wolverine.HTTP 1.7
        // Helps Wolverine 
        return CreationResponse.For(new TodoCreated(todo.Id), $"/todoitems/{tenant}/{todo.Id}");
    }

    #endregion

    #region sample_invoke_for_tenant

    // While this is still valid....
    [WolverineDelete("/todoitems/{tenant}/longhand")]
    public static async Task Delete(
        string tenant, 
        DeleteTodo command, 
        IMessageBus bus)
    {
        // Invoke inline for the specified tenant
        await bus.InvokeForTenantAsync(tenant, command);
    }
    
    // Wolverine.HTTP 1.7 added multi-tenancy support so
    // this short hand works without the extra jump through
    // "Wolverine as Mediator"
    [WolverineDelete("/todoitems/{tenant}")]
    public static void Delete(
        DeleteTodo command, IDocumentSession session)
    {
        // Just mark this document as deleted,
        // and Wolverine middleware takes care of the rest
        // including the multi-tenancy detection now
        session.Delete<Todo>(command.Id);
    }

    #endregion
}


public static class TodoCreatedHandler
{
    public static void Handle(DeleteTodo command, IDocumentSession session)
    {
        session.Delete<Todo>(command.Id);
    }
 
}


