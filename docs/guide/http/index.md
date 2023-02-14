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

snippet: sample_bootstrapping_wolverine_http

Do note that the only thing in that sample that pertains to `WolverineFx.Http` itself is the call to `IEndpointRouteBuilder.MapWolverineEndpoints()`.

Let's move on to "Hello, World" with a new Wolverine http endpoint from this class we'll add to the sample project:

snippet: sample_hello_world_with_wolverine_http

At application startup, WolverineFx.Http will find the `HelloEndpoint.Get()` method and treat it as a Wolverine http endpoint with
the route pattern `GET: /` specified in the `[WolverineGet]` attribute.

As you'd expect, that route will write the return value back to the HTTP response and behave as specified
by this [Alba](https://jasperfx.github.io/alba) specification:

snippet: sample_testing_hello_world_for_http

Moving on to the actual `Todo` problem domain, let's assume we've got a class like this:

snippet: sample_Todo

In a sample class called [TodoEndpoints](https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/Endpoints.cs)
let's add an HTTP service endpoint for listing all the known `Todo` documents:

snippet: sample_get_to_json

As you'd guess, this method will serialize all the known `Todo` documents from the database into the HTTP response
and return a 200 status code. In this particular case the code is a little bit noisier than the Minimal API equivalent,
but that's okay, because you can happily use Minimal API and WolverineFx.Http together in the same project. WolverineFx.Http, however,
will shine in more complicated endpoints. 

Consider this endpoint just to return the data for a single `Todo` document:

snippet: sample_GetTodo

At this point it's effectively de rigueur for any web service to support [OpenAPI](https://www.openapis.org/) documentation directly
in the service. Fortunately, WolverineFx.Http is able to glean most of the necessary metadata to support OpenAPI documentation
with [Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) from the method signature up above. The method up above will also cleanly set a status code of 404 if the requested
`Todo` document does not exist.

Now, the bread and butter for WolverineFx.Http is using it in conjunction with Wolverine itself. In this sample, let's create a new
`Todo` based on submitted data, but also publish a new event message with Wolverine to do some background processing after the HTTP
call succeeds. And, oh, yeah, let's make sure this endpoint is actively using Wolverine's [transactional outbox](/guide/durability/) support for consistency:

snippet: sample_posting_new_todo_with_middleware

The endpoint code above is automatically enrolled in the Marten transactional middleware by simple virtue of having a
dependency on Marten's `IDocumentSession`. By also taking in the `IMessageBus` dependency, WolverineFx.Http is wrapping the
transactional outbox behavior around the method so that the `TodoCreated` message is only sent after the database transaction
succeeds.

::: tip
WolverineFx.Http allows you to place any number of endpoint methods on any public class that follows the naming conventions,
but we strongly recommend isolating any kind of complicated endpoint method to its own endpoint class.
:::git st

Lastly for this page, consider the need to update a `Todo` from a `PUT` call. Your HTTP endpoint may vary its
handling and response by whether or not the document actually exists. Just to show off Wolverine's "composite handler" functionality
and also how WolverineFx.Http supports middleware, consider this more complex endpoint:

snippet: sample_UpdateTodoEndpoint


## How it Works

## Discovery

## OpenAPI Metadata