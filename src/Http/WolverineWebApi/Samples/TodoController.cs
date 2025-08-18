using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Marten;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using JasperFx;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;
using Wolverine.Runtime;

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

#region sample_update_with_required_entity

public record UpdateRequest(string Name, bool IsComplete);

public static class UpdateEndpoint
{
    // Find required Todo entity for the route handler below
    public static Task<Todo?> LoadAsync(int id, IDocumentSession session)
        => session.LoadAsync<Todo>(id);

    [WolverinePut("/todos/{id:int}")]
    public static StoreDoc<Todo> Put(
        // Route argument
        int id,

        // The request body
        UpdateRequest request,

        // Entity loaded by the method above,
        // but note the [Required] attribute
        [Required] Todo? todo)
    {
        todo.Name = request.Name;
        todo.IsComplete = request.IsComplete;

        return MartenOps.Store(todo);
    }
}

#endregion

public static class Update2Endpoint
{
    public static async Task<(Todo? todo, IResult response)> LoadAsync(int id, UpdateRequest command, IDocumentSession session)
    {
        var todo = await session.LoadAsync<Todo>(id);
        return todo != null
            ? (todo, response: new WolverineContinue())
            : (todo, response: Results.NotFound());
    }

    [Tags("Todos")]
    [WolverinePut("/todos2/{id}")]
    public static Todo Put(int id, UpdateRequest request, Todo todo, IDocumentSession session)
    {
        todo.Name = request.Name;
        todo.IsComplete = request.IsComplete;
        session.Store(todo);
        return todo;
    }
}

public static class UpdateEndpointWithValidation
{
    // Find required Todo entity for the route handler below
    public static Task<Todo?> LoadAsync(int id, IDocumentSession session)
        => session.LoadAsync<Todo>(id);

    public static IResult Validate([Required] Todo? todo)
    {
        return todo.IsComplete
            ? Results.ValidationProblem([new("IsComplere", ["Completed Todo cannot be updated"])])
            : WolverineContinue.Result();
    }

    [WolverinePut("/todos/validation/{id:int}")]
    public static StoreDoc<Todo> Put(
        // Route argument
        int id,

        // The request body
        UpdateRequest request,

        // Entity loaded by the method above,
        // but note the [Required] attribute
        [Required] Todo? todo)
    {
        todo.Name = request.Name;
        todo.IsComplete = request.IsComplete;

        return MartenOps.Store(todo);
    }
}

public static class UpdateEndpointWithMiddleware
{
    [WolverinePut("/todos/middleware/{id:int}")]
    public static StoreDoc<Todo> Put(
        // Route argument
        int id,

        // The request body
        UpdateRequest request,

        // Entity loaded by the method above,
        // but note the [Required] attribute
        [Required] Todo? todo)
    {
        todo.Name = request.Name;
        todo.IsComplete = request.IsComplete;

        return MartenOps.Store(todo);
    }
}

public class LoadTodoMiddleware
{
    // Find required Todo entity for the route handler below
    public static Task<Todo?> LoadAsync(int id, IDocumentSession session)
        => session.LoadAsync<Todo>(id);
}

public static class UpdateEndpointWithPolicy
{
    [WolverinePut("/todos/policy/{id:int}")]
    public static StoreDoc<Todo> Put(
        // Route argument
        int id,

        // The request body
        UpdateRequest request,

        // Entity loaded by the method above,
        // but note the [Required] attribute
        [Required] Todo? todo)
    {
        todo.Name = request.Name;
        todo.IsComplete = request.IsComplete;

        return MartenOps.Store(todo);
    }
}

public class LoadTodoPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            if (chain.EndpointType == typeof(UpdateEndpointWithPolicy))
            {
                chain.Middleware.Insert(0, new MethodCall(typeof(LoadTodoMiddleware), nameof(LoadTodoMiddleware.LoadAsync)));
            }
        }
    }
}