# Http Services with Wolverine

::: info
Wolverine.Http is strictly designed for building web services, but you can happily mix and 
match Wolverine HTTP endpoints with ASP.Net Core MVC handling Razor views. 
:::

Server side applications are frequently built with some mixture of HTTP web services, asynchronous processing, and
asynchronous messaging. Wolverine by itself can help you with the asynchronous processing through its [local queue functionality](/guide/messaging/transports/local),
and it certainly covers all common [asynchronous messaging](/guide/messaging/introduction) requirements. 

For a simplistic example, let's say that we're inevitably building a "Todo" application where we want a web service
endpoint that allows our application to create a new `Todo` entity, save it to a database, and raise an `TodoCreated` event
that will be handled later and off to the side by Wolverine.

Even in this simple example usage, that endpoint *should* be developed such that the creation of the new `Todo` entity
and the corresponding `TodoCreated` event message either succeed or fail together to avoid putting the system into an 
inconsistent state. That's a perfect use case for Wolverine's [transactional outbox](/guide/durability/). While the Wolverine 
team believes that Wolverine's outbox functionality is significantly easier to use outside of the context of message handlers
than other .NET messaging tools, it's still easiest to use within the context of a message handler, so let's just build
out a Wolverine message handler for the `CreateTodo` command:

<!-- snippet: sample_create_todo_handler -->
<a id='snippet-sample_create_todo_handler'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L59-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_create_todo_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Okay, but we still need to expose a web service endpoint for this functionality. We *could* utilize Wolverine within an MVC controller
as a "mediator" tool like so:

<!-- snippet: sample_TodoController_delegating_to_Wolverine -->
<a id='snippet-sample_todocontroller_delegating_to_wolverine'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L11-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_todocontroller_delegating_to_wolverine' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or we could do the same thing with Minimal API:

<!-- snippet: sample_wolverine_within_minimal_api -->
<a id='snippet-sample_wolverine_within_minimal_api'></a>
```cs
// app in this case is a WebApplication object
app.MapPost("/todoitems", async (CreateTodo command, IMessageBus bus) =>
{
    var todo = await bus.InvokeAsync<Todo>(command);
    return Results.Created($"/todoitems/{todo.Id}", todo);
}).Produces<Todo>(201);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L34-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_within_minimal_api' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

While the code above is certainly functional, and many teams are succeeding today using a similar strategy with older tools like
[MediatR](https://github.com/jbogard/MediatR), the Wolverine team thinks there are some areas to improve in the code above:

1. When you look into the internals of the runtime, there's some potentially unnecessary performance overhead as every single
   call to that web service does service locations and dictionary lookups that could be eliminated
2. There's some opportunity to reduce object allocations on each request -- and that *can* be a big deal for performance and scalability
3. It's not that bad, but there's some boilerplate code above that serves no purpose at runtime but helps in the generation
   of [OpenAPI documentation](https://www.openapis.org/) through Swashbuckle

At this point, let's look at some tooling in the `WolverineFx.Http` Nuget library that can help you incorporate Wolverine into
ASP.Net Core applications in a potentially more successful way than trying to "just" use Wolverine as a mediator tool.

After adding the `WolverineFx.Http` Nuget to our Todo web service, I could use this option for a little bit more 
efficient delegation to the underlying Wolverine message handler:

<!-- snippet: sample_map_route_to_wolverine_handler -->
<a id='snippet-sample_map_route_to_wolverine_handler'></a>
```cs
// This is *almost* an equivalent, but you'd get a status
// code of 200 instead of 201. If you care about that anyway.
app.MapPostToWolverine<CreateTodo, Todo>("/todoitems");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L49-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_map_route_to_wolverine_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The code up above is very close to a functional equivalent to our early Minimal API or MVC Controller usage, but there's a 
couple differences:

1. In this case the HTTP endpoint will return a status code of `200` instead of the slightly more correct `201` that denotes
   a creation. **Most of use aren't really going to care, but we'll come back to this a little later**
2. In the call to `MapPostToWolverine()`, Wolverine.HTTP is able to make a couple performance optimizations that completely
   eliminates any usage of the application's IoC container at runtime and bypasses some dictionary lookups and object allocation
   that would have to occur in the simple "mediator" approach

I personally find the indirection of delegating to a mediator tool to add more code ceremony and indirection than I prefer,
but many folks like that approach because of how bloated MVC Controller types can become in enterprise systems over time. What
if instead we just had a much cleaner way to code an HTTP endpoint that *still* helped us out with OpenAPI documentation?

That's where the Wolverine.Http ["endpoint" model](/guide/http/endpoints) comes into play. Let's take the same Todo creation
endpoint and use Wolverine to build an HTTP endpoint:

<!-- snippet: sample_using_wolverine_endpoint_for_create_todo -->
<a id='snippet-sample_using_wolverine_endpoint_for_create_todo'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L82-L114' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_wolverine_endpoint_for_create_todo' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The code above will actually generate the exact same OpenAPI documentation as the MVC Controller or Minimal API samples 
earlier in this post, but there's significantly less boilerplate code needed to expose that information. Instead, Wolverine.Http
relies on type signatures to "know" what the OpenAPI metadata for an endpoint should be. In conjunction with Wolverine's [Marten
integration](/guide/durability/marten/) (or Wolverine's [EF Core integration](/guide/durability/efcore) too!),
you potentially get a very low ceremony approach to writing HTTP services that *also* utilizes Wolverine's [durable outbox](/guide/durability/)
without giving up anything in regards to crafting effective and accurate OpenAPI metadata about your services.







