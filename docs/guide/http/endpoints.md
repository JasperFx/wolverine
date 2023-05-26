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
public static Results PostJson(Question question)
{
    return new Results
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
public static Task<Results> PostJsonAsync(Question question)
{
    var results = new Results
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

## Routing

::: warning
The route argument to method name matching is case sensitive.
:::

Wolverine HTTP endpoints need to be decorated with one of the `[WolverineVerb("route")]` attributes
that expresses the routing argument path in standard ASP.Net Core syntax (i.e., the same as when using
MVC Core or Minimal API).

If a parameter argument to the HTTP handler method *exactly matches* a route argument, Wolverine will
treat that as a route argument and pass the route argument value at runtime from ASP.Net Core to your
handler method. To make that concrete, consider this simple case from the test suite:

sample: sample_using_string_route_parameter

In the sample above, the `name` argument will be the value of the route argument
at runtime. Here's another example, but this time using a numeric value:

<!-- snippet: sample_using_numeric_route_parameter -->
<a id='snippet-sample_using_numeric_route_parameter'></a>
```cs
[WolverineGet("/age/{age}")]
public static string IntRouteArgument(int age)
{
    return $"Age is {age}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L35-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_numeric_route_parameter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The following code snippet from `WolverineFx.Http` itself shows the valid route
parameter types that are supported at this time:

<!-- snippet: sample_supported_route_parameter_types -->
<a id='snippet-sample_supported_route_parameter_types'></a>
```cs
public static readonly Dictionary<Type, string> TypeOutputs = new()
{
    { typeof(bool), "bool" },
    { typeof(byte), "byte" },
    { typeof(sbyte), "sbyte" },
    { typeof(char), "char" },
    { typeof(decimal), "decimal" },
    { typeof(float), "float" },
    { typeof(short), "short" },
    { typeof(int), "int" },
    { typeof(double), "double" },
    { typeof(long), "long" },
    { typeof(ushort), "ushort" },
    { typeof(uint), "uint" },
    { typeof(ulong), "ulong" },
    { typeof(Guid), typeof(Guid).FullName },
    { typeof(DateTime), typeof(DateTime).FullName },
    { typeof(DateTimeOffset), typeof(DateTimeOffset).FullName }
};
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/CodeGen/RouteHandling.cs#L52-L74' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_supported_route_parameter_types' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Wolverine will return a 404 status code if a route parameter cannot
be correctly parsed. So passing "ABC" into what is expected to be an
integer will result in a 404 response.
:::


## Working with QueryString

::: tip
Wolverine can handle both nullable types and the primitive values here. So 
`int` and `int?` are both valid. In all cases, if the query string does not exist -- or
cannot be parsed -- the value passed to your method will be the `default` for whatever that
type is.
:::

Wolverine supports passing query string values to your HTTP method arguments for
the exact same set of value types supported for route arguments. In this case,
Wolverine treats any value type parameter where the parameter name does not
match a route argument name as coming from the HTTP query string. 

When Wolverine does the runtime matching, it's using the exact parameter name as the 
query string key. Here's a quick sample:

<!-- snippet: sample_using_string_value_as_query_string -->
<a id='snippet-sample_using_string_value_as_query_string'></a>
```cs
[WolverineGet("/querystring/string")]
public static string UsingQueryString(string name) // name is from the query string
{
    return name.IsEmpty() ? "Name is missing" : $"Name is {name}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L45-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_string_value_as_query_string' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the corresponding tests:

<!-- snippet: sample_query_string_usage -->
<a id='snippet-sample_query_string_usage'></a>
```cs
[Fact]
public async Task use_string_querystring_hit()
{
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/string?name=Magic");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    body.ReadAsText().ShouldBe("Name is Magic");
}

[Fact]
public async Task use_string_querystring_miss()
{
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/string");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    body.ReadAsText().ShouldBe("Name is missing");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/end_to_end.cs#L149-L175' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_query_string_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Working with JSON

::: warning
At this point WolverineFx.Http **only** supports `System.Text.Json` for the HTTP endpoints,
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
        x.WithRequestHeader("accepts", "application/json");
    });

    var result = await response.ReadAsJsonAsync<Results>();

    result.Product.ShouldBe(12);
    result.Sum.ShouldBe(7);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/posting_json.cs#L12-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_post_json_happy_path' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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

