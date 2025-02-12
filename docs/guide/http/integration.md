# ASP.Net Core Integration

::: tip
WolverineFx.HTTP is an alternative to Minimal API or MVC Core for crafting HTTP service endpoints, but absolutely tries to be a good citizen within the greater
ASP.Net Core ecosystem and heavily utilizes much of the ASP.Net Core technical foundation. It is also perfectly possible to use
any mix of WolverineFx.HTTP, Minimal API, and MVC Core controllers within the same code base as you see fit.
:::

The `WolverineFx.HTTP` library extends Wolverine's runtime model to writing HTTP services with ASP.Net Core. As a quick sample, start
a new project with:

```bash
dotnet new webapi
```

Then add the `WolverineFx.HTTP` dependency with:

```bash
dotnet add package WolverineFx.HTTP
```

::: tip
The [sample project for this page is on GitHub](https://github.com/JasperFx/wolverine/tree/main/src/Samples/TodoWebService/TodoWebService).
:::

From there, let's jump into the application bootstrapping. Stealing the [sample "Todo" project idea from the Minimal API documentation](https://learn.microsoft.com/en-us/aspnet/core/tutorials/min-web-api?view=aspnetcore-7.0&tabs=visual-studio) (and
shifting to [Marten](https://martendb.io) for persistence just out of personal preference), this is the application bootstrapping:

<!-- snippet: sample_bootstrapping_wolverine_http -->
<a id='snippet-sample_bootstrapping_wolverine_http'></a>
```cs
using Marten;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// Adding Marten for persistence
builder.Services.AddMarten(opts =>
    {
        opts.Connection(builder.Configuration.GetConnectionString("Marten"));
        opts.DatabaseSchemaName = "todo";
    })
    .IntegrateWithWolverine();

builder.Services.AddResourceSetupOnStartup();

// Wolverine usage is required for WolverineFx.Http
builder.Host.UseWolverine(opts =>
{
    // This middleware will apply to the HTTP
    // endpoints as well
    opts.Policies.AutoApplyTransactions();

    // Setting up the outbox on all locally handled
    // background tasks
    opts.Policies.UseDurableLocalQueues();
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Let's add in Wolverine HTTP endpoints to the routing tree
app.MapWolverineEndpoints();

return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/Program.cs#L1-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_wolverine_http' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Do note that the only thing in that sample that pertains to `WolverineFx.Http` itself is the call to `IEndpointRouteBuilder.MapWolverineEndpoints()`.

Let's move on to "Hello, World" with a new Wolverine http endpoint from this class we'll add to the sample project:

<!-- snippet: sample_hello_world_with_wolverine_http -->
<a id='snippet-sample_hello_world_with_wolverine_http'></a>
```cs
public class HelloEndpoint
{
    [WolverineGet("/")]
    public string Get() => "Hello.";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/HelloEndpoint.cs#L5-L13' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_hello_world_with_wolverine_http' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At application startup, WolverineFx.Http will find the `HelloEndpoint.Get()` method and treat it as a Wolverine http endpoint with
the route pattern `GET: /` specified in the `[WolverineGet]` attribute.

As you'd expect, that route will write the return value back to the HTTP response and behave as specified
by this [Alba](https://jasperfx.github.io/alba) specification:

<!-- snippet: sample_testing_hello_world_for_http -->
<a id='snippet-sample_testing_hello_world_for_http'></a>
```cs
[Fact]
public async Task hello_world()
{
    var result = await _host.Scenario(x =>
    {
        x.Get.Url("/");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    result.ReadAsText().ShouldBe("Hello.");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebServiceTests/end_to_end.cs#L34-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_testing_hello_world_for_http' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Moving on to the actual `Todo` problem domain, let's assume we've got a class like this:

<!-- snippet: sample_Todo -->
<a id='snippet-sample_todo'></a>
```cs
public class Todo
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool IsComplete { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/Endpoints.cs#L7-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_todo' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In a sample class called [TodoEndpoints](https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/Endpoints.cs)
let's add an HTTP service endpoint for listing all the known `Todo` documents:

<!-- snippet: sample_get_to_json -->
<a id='snippet-sample_get_to_json'></a>
```cs
[WolverineGet("/todoitems")]
public static Task<IReadOnlyList<Todo>> Get(IQuerySession session)
    => session.Query<Todo>().ToListAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/Endpoints.cs#L28-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_get_to_json' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As you'd guess, this method will serialize all the known `Todo` documents from the database into the HTTP response
and return a 200 status code. In this particular case the code is a little bit noisier than the Minimal API equivalent,
but that's okay, because you can happily use Minimal API and WolverineFx.Http together in the same project. WolverineFx.Http, however,
will shine in more complicated endpoints.

Consider this endpoint just to return the data for a single `Todo` document:

<!-- snippet: sample_GetTodo -->
<a id='snippet-sample_gettodo'></a>
```cs
// Wolverine can infer the 200/404 status codes for you here
// so there's no code noise just to satisfy OpenAPI tooling
[WolverineGet("/todoitems/{id}")]
public static Task<Todo?> GetTodo(int id, IQuerySession session, CancellationToken cancellation)
    => session.LoadAsync<Todo>(id, cancellation);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/Endpoints.cs#L40-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_gettodo' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At this point it's effectively de rigueur for any web service to support [OpenAPI](https://www.openapis.org/) documentation directly
in the service. Fortunately, WolverineFx.Http is able to glean most of the necessary metadata to support OpenAPI documentation
with [Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) from the method signature up above. The method up above will also cleanly set a status code of 404 if the requested
`Todo` document does not exist.

Now, the bread and butter for WolverineFx.Http is using it in conjunction with Wolverine itself. In this sample, let's create a new
`Todo` based on submitted data, but also publish a new event message with Wolverine to do some background processing after the HTTP
call succeeds. And, oh, yeah, let's make sure this endpoint is actively using Wolverine's [transactional outbox](/guide/durability/) support for consistency:

<!-- snippet: sample_posting_new_todo_with_middleware -->
<a id='snippet-sample_posting_new_todo_with_middleware'></a>
```cs
[WolverinePost("/todoitems")]
public static async Task<IResult> Create(CreateTodo command, IDocumentSession session, IMessageBus bus)
{
    var todo = new Todo { Name = command.Name };
    session.Store(todo);

    // Going to raise an event within out system to be processed later
    await bus.PublishAsync(new TodoCreated(todo.Id));

    return Results.Created($"/todoitems/{todo.Id}", todo);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/Endpoints.cs#L50-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_posting_new_todo_with_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The endpoint code above is automatically enrolled in the Marten transactional middleware by simple virtue of having a
dependency on Marten's `IDocumentSession`. By also taking in the `IMessageBus` dependency, WolverineFx.Http is wrapping the
transactional outbox behavior around the method so that the `TodoCreated` message is only sent after the database transaction
succeeds.

::: tip
WolverineFx.Http allows you to place any number of endpoint methods on any public class that follows the naming conventions,
but we strongly recommend isolating any kind of complicated endpoint method to its own endpoint class.
:::

Lastly for this page, consider the need to update a `Todo` from a `PUT` call. Your HTTP endpoint may vary its
handling and response by whether or not the document actually exists. Just to show off Wolverine's "composite handler" functionality
and also how WolverineFx.Http supports middleware, consider this more complex endpoint:

<!-- snippet: sample_UpdateTodoEndpoint -->
<a id='snippet-sample_updatetodoendpoint'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/Endpoints.cs#L81-L102' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_updatetodoendpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## How it Works

WolverineFx.Http takes advantage of the ASP.Net Core endpoint routing to add additional routes to the ASP.Net Core's routing tree. In Wolverine's
case though, the underlying `RequestDelegate` is compiled at runtime (or ahead of time for faster cold starts!) with the same
code weaving strategy as Wolverine's message handling. Wolverine is able to utilize the same [middleware model as the message handlers](/guide/handlers/middleware),
with some extensions for recognizing the ASP.Net Core IResult model.

## Discovery

::: tip
The HTTP endpoint method discovery is very similar to the [handler discovery](/guide/handlers/discovery) and will scan
the same assemblies as with the handlers.
:::

WolverineFx.Http discovers endpoint methods automatically by doing type scanning within your application.

The assemblies scanned are:

1. The entry assembly for your application
2. Any assembly marked with the `[assembly: WolverineModule]` attribute
3. Any assembly that is explicitly added in the `UseWolverine()` configuration as a handler assembly as shown in the following sample code:

<!-- snippet: sample_programmatically_scan_assemblies -->
<a id='snippet-sample_programmatically_scan_assemblies'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // This gives you the option to programmatically
        // add other assemblies to the discovery of HTTP endpoints
        // or message handlers
        var assembly = Assembly.Load("my other assembly name that holds HTTP endpoints or handlers");
        opts.Discovery.IncludeAssembly(assembly);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/DocumentationSamples.cs#L11-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_programmatically_scan_assemblies' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
Wolverine 1.6.0 added the looser discovery rules to just go look for any method on public, concrete types that is
decorated with a Wolverine route attribute.
:::

In the aforementioned assemblies, Wolverine will look for **public, concrete, closed** types whose names are
suffixed by `Endpoint` or `Endpoints` **and also any public, concrete class with methods that are decorated by any `[WolverineVerb]` attribute**. Within these types, Wolverine is looking for **public** methods that
are decorated with one of Wolverine's HTTP method attributes:

- `[WolverineGet]`
- `[WolverinePut]`
- `[WolverinePost]`
- `[WolverineDelete]`
- `[WolverineOptions]`
- `[WolverineHead]`

The usage is suspiciously similar to the older `[HttpGet]` type attributes in MVC Core.

## OpenAPI Metadata

Wolverine is trying to replicate the necessary OpenAPI to fully support Swashbuckle usage with Wolverine endpoints. This is
a work in process though. At this point it can at least expose:

- HTTP status codes
- HTTP methods
- Input and output types when an http method either takes in JSON bodies or writes JSON responses
- Authorization rules -- or really any ASP.Net Core attribute like `[Authorize]`
