# Http Services with Wolverine

::: info
Wolverine.Http is strictly designed for building web services, but you can happily mix and 
match Wolverine HTTP endpoints with ASP.Net Core MVC handling Razor views. 
:::

::: warning
If you are moving to Wolverine.Http from 2.* or earlier, just know that there is now a required `IServiceCollection.AddWolverineHttp()`
call in your `Program.Main()` bootstrapping. Wolverine.Http will "remind" you on startup by throwing an exception if the extra service registration is missing. This was a side effect
of the change to support `ServiceProvider` and other IoC tools. 
:::

Server side applications are frequently built with some mixture of HTTP web services, asynchronous processing, and
asynchronous messaging. Wolverine by itself can help you with the asynchronous processing through its [local queue functionality](/guide/messaging/transports/local),
and it certainly covers all common [asynchronous messaging](/guide/messaging/introduction) requirements. 

Wolverine also has its Wolverine.Http library
that utilizes Wolverine's execution pipeline for ASP.Net Core web services. Besides generally being a lower code ceremony
option to MVC Core or Minimal API, Wolverine.HTTP provides very strong integration with Wolverine's transactional inbox/outbox 
support for durable messaging (something that has in the past been very poorly supported if at all by older .NET messaging tools) as a very effective
tooling solution for Event Driven Architectures that include HTTP services. 

Moreover, Wolverine.HTTP's coding model is conducive to "vertical slice architecture" approaches with significantly lower
code ceremony than other .NET web frameworks. Lastly, Wolverine.HTTP can help you create code where the business or workflow
logic is easily unit tested in isolation without having to resort to complicated layering in code or copious usage of mock
objects in your test code.

For a simplistic example, let's say that we're inevitably building a "Todo" application where we want a web service
endpoint that allows our application to create a new `Todo` entity, save it to a database, and raise an `TodoCreated` event
that will be handled later and off to the side by Wolverine.

## Getting Started

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L61-L78' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_create_todo_handler' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L14-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_todocontroller_delegating_to_wolverine' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L37-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_within_minimal_api' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L51-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_map_route_to_wolverine_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The code up above is very close to a functional equivalent to our early Minimal API or MVC Controller usage, but there's a 
couple differences:

1. In this case the HTTP endpoint will return a status code of `200` instead of the slightly more correct `201` that denotes
   a creation. **Most of us aren't really going to care, but we'll come back to this a little later**
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/TodoController.cs#L84-L116' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_wolverine_endpoint_for_create_todo' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The code above will actually generate the exact same OpenAPI documentation as the MVC Controller or Minimal API samples 
earlier in this post, but there's significantly less boilerplate code needed to expose that information. Instead, Wolverine.Http
relies on type signatures to "know" what the OpenAPI metadata for an endpoint should be. In conjunction with Wolverine's [Marten
integration](/guide/durability/marten/) (or Wolverine's [EF Core integration](/guide/durability/efcore) too!),
you potentially get a very low ceremony approach to writing HTTP services that *also* utilizes Wolverine's [durable outbox](/guide/durability/)
without giving up anything in regards to crafting effective and accurate OpenAPI metadata about your services.

## Eager Warmup <Badge type="tip" text="4.1" />

Wolverine.HTTP has a known issue with applications that make simultaneous requests to the same endpoint
at start up where the runtime code generation can blow up if the first requests come in together. While the Wolverine team
works on this, the simple amelioration is to either "just" pre-generate the code ahead of time. See [Working with Code Generation](/guide/codegen) for more information on this.

Or, you can opt for `Eager` initialization of the HTTP endpoints to side step this problem in development
when pre-generating types isn't viable:

<!-- snippet: sample_eager_http_warmup -->
<a id='snippet-sample_eager_http_warmup'></a>
```cs
var app = builder.Build();

app.MapWolverineEndpoints(x => x.WarmUpRoutes = RouteWarmup.Eager);
    
return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/CrazyStartingWebApp/Program.cs#L21-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_eager_http_warmup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Using the HttpContext.RequestServices <Badge type="tip" text="5.0" />

::: tip
The opt in behavior to share the scoped services with the rest of the AspNetCore pipeline is useful
for using Wolverine endpoints underneath AspNetCore middleware that "smuggles" state through the IoC container.

Custom multi-tenancy middleware or custom authorization or other security middleware frequently does this. We think
this will be helpful for mixed systems where Wolverine.HTTP is used for some routes while other routes are served
by MVC Core or Minimal API or even some other kind of AspNetCore `Endpoint`.
:::

By default, any time [Wolverine has to revert to using a service locator](/guide/codegen.html#wolverine-code-generation-and-ioc) 
to generate the adapter code for an HTTP endpoint, Wolverine is using an isolated `IServiceScope` (or Lamar `INestedContainer`) within the generated code.

But, with Wolverine 5.0+ you can opt into Wolverine just using the `HttpContext.RequestServices` so that you
can share services with AspNetCore middleware. You can also configure *some* service types to be pulled from
the `HttpContext.RequestServices` even if Wolverine is otherwise generating more efficient constructor calls 
for all other dependencies. Here's an example using both of these opt in behaviors:

<!-- snippet: sample_bootstrapping_with_httpcontext_request_services -->
<a id='snippet-sample_bootstrapping_with_httpcontext_request_services'></a>
```cs
var builder = WebApplication.CreateBuilder();

builder.UseWolverine(opts =>
{
    // more configuration
});

// Just pretend that this IUserContext is being 
builder.Services.AddScoped<IUserContext, UserContext>();
builder.Services.AddWolverineHttp();

var app = builder.Build();

// Custom middleware that is somehow configuring our IUserContext
// that might be getting used within 
app.UseMiddleware<MyCustomUserMiddleware>();

app.MapWolverineEndpoints(opts =>
{
    // Opt into using the shared HttpContext.RequestServices scoped
    // container any time Wolverine has to use a service locator
    opts.ServiceProviderSource = ServiceProviderSource.FromHttpContextRequestServices;
    
    // OR this is the default behavior to be backwards compatible:
    opts.ServiceProviderSource = ServiceProviderSource.IsolatedAndScoped;
    
    // We're telling Wolverine that the IUserContext should always
    // be pulled from HttpContext.RequestServices
    // and this happens regardless of the ServerProviderSource!
    opts.SourceServiceFromHttpContext<IUserContext>();
});

return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/CodeGeneration/service_location_assertions.cs#L396-L432' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_httpcontext_request_services' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Notice the call to `SourceServiceFromHttpContext<T>()`. That directs Wolverine.HTTP to always pull the service
`T` from the `HttpContext.RequestServices` scoped container so that Wolverine.HTTP can play nicely with custom AspNetCore
middleware or whatever else you have around your Wolverine.HTTP endpoints. 

::: warning
The Wolverine team believes that smuggling important state between upstream middleware and downstream handlers
leads to code that is hard to reason about and hence, potentially buggy in real life usage. Alas, you could easily
need this functionality in the real world, so here you go. 
:::


