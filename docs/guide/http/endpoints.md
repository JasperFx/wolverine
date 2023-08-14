# HTTP Endpoints

::: warning
In all cases, the resource returned from a Wolverine HTTP endpoint method **is not automatically
published as a Wolverine cascaded message**. At this moment you will have to directly use `IMessageBus`
in your method signature to publish messages.
:::

First, a little terminology about Wolverine HTTP endpoints. Consider the following endpoint method:

<!-- snippet: sample_simple_wolverine_http_endpoint -->
<a id='snippet-sample_simple_wolverine_http_endpoint'></a>
```cs
[WolverinePost("/question")]
public static ArithmeticResults PostJson(Question question)
{
    return new ArithmeticResults
    {
        Sum = question.One + question.Two,
        Product = question.One * question.Two
    };
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L73-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simple_wolverine_http_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the method signature above, `Question` is the "request" type (the payload sent from the client to the server) and `Answer` is the "resource" type (what is being returned to the client).
If instead that method were asynchronous like this:

<!-- snippet: sample_simple_wolverine_http_endpoint_async -->
<a id='snippet-sample_simple_wolverine_http_endpoint_async'></a>
```cs
[WolverinePost("/question2")]
public static Task<ArithmeticResults> PostJsonAsync(Question question)
{
    var results = new ArithmeticResults
    {
        Sum = question.One + question.Two,
        Product = question.One * question.Two
    };

    return Task.FromResult(results);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L87-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simple_wolverine_http_endpoint_async' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The resource type is still `Answer`. Likewise, if an endpoint returns `ValueTask<Answer>`, the resource type
is also `Answer`, and Wolverine will worry about the asynchronous (or `return Task.CompletedTask;`) mechanisms
for you in the generated code.

## Legal Endpoint Signatures

::: info
It's actually possible to create custom conventions for 
:::

First off, every endpoint method must be a `public` method on a `public` type to accomodate the runtime code generation.
After that, you have quite a bit of flexibility. 

In terms of what the legal parameters to your endpoint method, Wolverine uses these rules *in order of precedence*
to determine how to source that parameter at runtime:

| Type or Description                        | Behavior                                                                                                                 |
|--------------------------------------------|--------------------------------------------------------------------------------------------------------------------------|
| Decorated with `[FromServices]`            | The argument is resolved as an IoC service                                                                               |
| `IMessageBus`                              | Creates a new Wolverine message bus object                                                                               |
| `HttpContext` or its members               | See the section below on accessing the HttpContext                                                                       |
| Parameter name matches a route parameter   | See the [routing page](/guide/http/routing) for more information                                                         |
| Decorated with `[FromHeader]`              | See [working with headers](/guide/http/headers) for more information                                                     |
| `string`, `int`, `Guid`, etc.              | All other "simple" .NET types are assumed to be [query string values](http://localhost:5050/guide/http/querystring.html) |
| The first concrete, "not simple" parameter | Deserializes the HTTP request body as JSON to this type                                                                  |
| Every thing else                           | Wolverine will try to source the type as an IoC service |

You can force Wolverine to ignore a parameter as the request body type by decorating
the parameter with the `[NotBody` attribute like this:

<!-- snippet: sample_using_not_body_attribute -->
<a id='snippet-sample_using_not_body_attribute'></a>
```cs
[WolverinePost("/notbody")]
// The Recorder parameter will be sourced as an IoC service
// instead of being treated as the HTTP request body
public string PostNotBody([NotBody] Recorder recorder)
{
    recorder.Actions.Add("Called AttributesEndpoints.Post()");
    return "all good";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/AttributeEndpoints.cs#L15-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_not_body_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In terms of the response type, you can use:

| Type                           | Body         | Status Code       | Notes                                                                          |
|--------------------------------|--------------|-------------------|--------------------------------------------------------------------------------|
| `void` / `Task` / `ValueTask`  | Empty        | 200               |                                                                                |
| `string`                       | "text/plain" | 200               | Writes the result to the response                                              |
| `int`                          | Empty        | Value of response |                                                                                |
| Type that implements `IResult` | Varies       | Varies            | The `IResult.ExecuteAsync()` method is executed                                |
| `CreationResponse` or subclass | JSON         | 201               | The response is serialized, and writes a `location` response header            |
| Any other type                 | JSON         | 200               | The response is serialized to JSON                                             |

In all cases up above, if the endpoint method is asynchronous using either `Task<T>` or `ValueTask<T>`, the `T` is the 
response type. In other words, a response of `Task<string>` has the same rules as a response of `string` and `ValueTask<int>`
behaves the same as a response of `int`. 

And now to complicate *everything*, but I promise this is potentially valuable, you can also use [Tuples](https://learn.microsoft.com/en-us/dotnet/api/system.tuple?view=net-7.0) as the return
type of an HTTP endpoint. In this case, the first item in the tuple is the official response type that is treated by the 
rules above. To make that concrete, consider this sample that we wrote in the introduction to Wolverine.Http:

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

In the case above, `TodoCreationResponse` is the first item in the tuple, so Wolverine treats that as 
the response for the HTTP endpoint. The second `TodoCreated` value in the tuple is treated as a [cascading message](http://localhost:5050/guide/messaging/transports/local.html)
that will be published through Wolverine's messaging (or a local queue depending on the routing).

How Wolverine handles those extra "return values" is the same [return value rules](/guide/handlers/return-values)
from the messaging handlers.

In the case of wanting to leverage Wolverine "return value" actions but you want your endpoint to return an
empty response body, you can use the `[Wolverine.Http.EmptyResponse]` attribute to tell Wolverine *not*
to use any return values as a the endpoint response and to return an empty response with a `204` status
code. Here's an example from the tests:

<!-- snippet: sample_using_EmptyResponse -->
<a id='snippet-sample_using_emptyresponse'></a>
```cs
[AggregateHandler]
[WolverinePost("/orders/ship"), EmptyResponse]
// The OrderShipped return value is treated as an event being posted
// to a Marten even stream
// instead of as the HTTP response body because of the presence of 
// the [EmptyResponse] attribute
public static OrderShipped Ship(ShipOrder command, Order order)
{
    return new OrderShipped();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L77-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_emptyresponse' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## JSON Handling

::: warning
At this point WolverineFx.Http **only** supports `System.Text.Json` as the default for the HTTP endpoints,
with the JSON settings coming from the application's Minimal API configuration.
:::

As explained up above, the "request" type to a Wolverine endpoint is the first argument that is:

1. Concrete
2. Not one of the value types that Wolverine considers for route or query string values
3. *Not* marked with `[FromServices]` from ASP.Net Core

If a parameter like this exists, that will be the request type, and will come
at runtime from deserializing the HTTP request body as JSON.

Likewise, any resource type besides strings will be written to the HTTP response body
as serialized JSON.

In this sample endpoint, both the request and resource types are dealt with by
JSON serialization. Here's the test from the actual Wolverine codebase:

<!-- snippet: sample_post_json_happy_path -->
<a id='snippet-sample_post_json_happy_path'></a>
```cs
[Fact]
public async Task post_json_happy_path()
{
    // This test is using Alba to run an end to end HTTP request
    // and interrogate the results
    var response = await Scenario(x =>
    {
        x.Post.Json(new Question { One = 3, Two = 4 }).ToUrl("/question");
        x.WithRequestHeader("accept", "application/json");
    });

    var result = await response.ReadAsJsonAsync<ArithmeticResults>();

    result.Product.ShouldBe(12);
    result.Sum.ShouldBe(7);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/posting_json.cs#L12-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_post_json_happy_path' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Using Newtonsoft.Json

::: TIP
Newtonsoft.Json is still much more battle hardened than System.Text.Json, and you may need
to drop back to Newtonsoft.Json for various scenarios. This feature was added specifically
at the request of F# developers.
:::

To opt into using Newtonsoft.Json for the JSON serialization of *HTTP endpoints*, you have this option within the call
to the `MapWolverineEndpoints()` configuration:

snippet: sample_use_newtonsoft_for_http_serialization

## Returning Strings

To create an endpoint that writes a string with `content-type` = "text/plain", just return a string as your resource type, so `string`, `Task<string>`, or `ValueTask<string>`
from your endpoint method like so:

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

## Using IResult

::: tip
The `IResult` mechanics are applied to the return value of any type that can be cast to `IResult`
:::

Wolverine will execute an ASP.Net Core `IResult` object returned from an HTTP endpoint method. 

<!-- snippet: sample_conditional_IResult_return -->
<a id='snippet-sample_conditional_iresult_return'></a>
```cs
[WolverineGet("/choose/color")]
public IResult Redirect(GoToColor request)
{
    switch (request.Color)
    {
        case "Red":
            return Results.Redirect("/red");

        case "Green":
            return Results.Redirect("/green");

        default:
            return Results.Content("Choose red or green!");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/DocumentationSamples.cs#L31-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conditional_iresult_return' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Using IoC Services

Wolverine HTTP endpoint methods happily support "method injection" of service
types that are known in the IoC container. If there's any potential for confusion
between the request type argument and what should be coming from the IoC
container, you can decorate parameters with the `[FromServices]` attribute
from ASP.Net Core to give Wolverine a hint. Otherwise, Wolverine is asking the underlying
Lamar container if it knows how to resolve the service from the parameter argument.


## Accessing HttpContext

Simply expose a parameter of any of these types to get either the current
`HttpContext` for the current request or children members of `HttpContext`:

1. `HttpContext`
2. `HttpRequest`
3. `HttpResponse`
4. `CancellationToken`
5. `ClaimsPrincipal`

You can also get at the trace identifier for the current `HttpContext` by a parameter like this:

<!-- snippet: sample_using_trace_identifier -->
<a id='snippet-sample_using_trace_identifier'></a>
```cs
[WolverineGet("/http/identifier")]
public string UseTraceIdentifier(string traceIdentifier)
{
    return traceIdentifier;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/HttpContextEndpoints.cs#L35-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_trace_identifier' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Customizing Parameter Handling

There's actually a way to customize how Wolverine handles parameters in HTTP endpoints to create your own conventions.
To do so, you'd need to write an implementation of the `IParameterStrategy` interface from Wolverine.Http:

<!-- snippet: sample_IParameterStrategy -->
<a id='snippet-sample_iparameterstrategy'></a>
```cs
/// <summary>
/// Apply custom handling to a Wolverine.Http endpoint/chain based on a parameter within the
/// implementing Wolverine http endpoint method
/// </summary>
/// <param name="variable">The Variable referring to the input of this parameter</param>
public interface IParameterStrategy
{
    bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable? variable);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/CodeGen/IParameterStrategy.cs#L7-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iparameterstrategy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As an example, let's say that you want any parameter of type `DateTimeOffset` that's named "now" to receive the current
system time. To do that, we can write this class:

<!-- snippet: sample_NowParameterStrategy -->
<a id='snippet-sample_nowparameterstrategy'></a>
```cs
public class NowParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.Name == "now" && parameter.ParameterType == typeof(DateTimeOffset))
        {
            // This is tying into Wolverine's code generation model
            variable = new Variable(typeof(DateTimeOffset),
                $"{typeof(DateTimeOffset).FullNameInCode()}.{nameof(DateTimeOffset.UtcNow)}");
            return true;
        }

        variable = default;
        return false;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/CustomParameter.cs#L10-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_nowparameterstrategy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and register that strategy within our `MapWolverineEndpoints()` set up like so:

<!-- snippet: sample_adding_custom_parameter_handling -->
<a id='snippet-sample_adding_custom_parameter_handling'></a>
```cs
// Customizing parameter handling
opts.AddParameterHandlingStrategy<NowParameterStrategy>();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L120-L125' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_custom_parameter_handling' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And lastly, here's the application within an HTTP endpoint for extra context:

<!-- snippet: sample_http_endpoint_receiving_now -->
<a id='snippet-sample_http_endpoint_receiving_now'></a>
```cs
[WolverineGet("/now")]
public static string GetNow(DateTimeOffset now) // using the custom parameter strategy for "now"
{
    return now.ToString();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/CustomParameterEndpoint.cs#L7-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_http_endpoint_receiving_now' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



