using Marten;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace WolverineWebApi.Samples;

#region sample_TodoController_delegating_to_Wolverine

public class TodoController : ControllerBase
{
    [HttpPost("/todoitems")]
    [ProducesResponseType(201, Type = typeof(Todo))]
    public async Task<ActionResult> Post(
        [FromBody] CreateTodo command, 
        [FromServices] IMessageBus bus)
    {
        // Delegate to Wolverine and capture the response
        // returned from the handler
        var todo = await bus.InvokeAsync<Todo>(command);
        return Created($"/todoitems/{todo.Id}", todo);
    }    
}

#endregion

public static class MinimalApiSample
{
    public static void MapToWolverine(WebApplication app)
    {
        #region sample_wolverine_within_minimal_api

        // app in this case is a WebApplication object
        app.MapPost("/todoitems", async (CreateTodo command, IMessageBus bus) =>
        {
            var todo = await bus.InvokeAsync<Todo>(command);
            return Results.Created($"/todoitems/{todo.Id}", todo);
        }).Produces<Todo>(201);

        #endregion
    }

    
    public static void MapToWolverine2(WebApplication app)
    {
        #region sample_map_route_to_wolverine_handler

        // This is *almost* an equivalent, but you'd get a status
        // code of 200 instead of 201. If you care about that anyway.
        app.MapPostToWolverine<CreateTodo, Todo>("/todoitems");

        #endregion
    }
}

#region sample_create_todo_handler

public class CreateTodoHandler
{
    public static (Todo, TodoCreated) Handle(CreateTodo command, IDocumentSession session)
    {
        var todo = new Todo { Name = command.Name };
        
        // Just telling Marten that there's a new entity to persist,
        // but I'm assuming that the transactional middleware in Wolverine is
        // handling the asynchronous persistence outside of this handler
        session.Store(todo);

        return (todo, new TodoCreated(todo.Id));
    }   
}

#endregion

public record CreateTodo(string Name);

public record TodoCreated(int Id);

#region sample_using_wolverine_endpoint_for_create_todo

// Introducing this special type just for the http response
// gives us back the 201 status code
public record TodoCreationResponse(int Id) 
    : CreationResponse("/todoitems/" + Id);

// The "Endpoint" suffix is meaningful, but you could use
// any name if you don't mind adding extra attributes or a marker interface
// for discovery
public static class TodoCreationEndpoint
{
    [WolverinePost("/todoitems")]
    public static (TodoCreationResponse, TodoCreated) Post(CreateTodo command, IDocumentSession session)
    {
        var todo = new Todo { Name = command.Name };
        
        // Just telling Marten that there's a new entity to persist,
        // but I'm assuming that the transactional middleware in Wolverine is
        // handling the asynchronous persistence outside of this handler
        session.Store(todo);

        // By Wolverine.Http conventions, the first "return value" is always
        // assumed to be the Http response, and any subsequent values are
        // handled independently
        return (
            new TodoCreationResponse(todo.Id), 
            new TodoCreated(todo.Id)
        );
    }
}

#endregion

public class Todo
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool IsComplete { get; set; }
}

public record UpdateRequest(string Name, bool IsComplete);

public static class UpdateEndpoint
{
    [WolverinePut("/todos/{id:int}")]
    public static Task Put(int id, UpdateRequest request, IDocumentSession session)
    {
        return Task.CompletedTask;
    }
}
