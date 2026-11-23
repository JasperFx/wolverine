## Using ProblemDetails

Wolverine has some first class support for the [ProblemDetails](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.problemdetails) specification in its [HTTP middleware model](./middleware).
Wolverine also has a [Fluent Validation middleware package](./validation#fluent-validation-middleware) for HTTP endpoints, but it's frequently valuable to write one
off, explicit validation for certain endpoints. 

Consider this contrived sample endpoint with explicit validation being done in a `Validate()` middleware method:

<!-- snippet: sample_problemdetailsusageendpoint -->
<a id='snippet-sample_problemdetailsusageendpoint'></a>
```cs
public class ProblemDetailsUsageEndpoint
{
    public ProblemDetails Validate(NumberMessage message)
    {
        // If the number is greater than 5, fail with a
        // validation message
        if (message.Number > 5)
            return new ProblemDetails
            {
                Detail = "Number is bigger than 5",
                Status = 400
            };

        // All good, keep on going!
        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/problems")]
    public static string Post(NumberMessage message)
    {
        return "Ok";
    }
}

public record NumberMessage(int Number);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/ProblemDetailsUsage.cs#L8-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_problemdetailsusageendpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine.Http now (as of 1.2.0) has a convention that sees a return value of `ProblemDetails` and looks at that as a
"continuation" to tell the http handler code what to do next. One of two things will happen:

1. If the `ProblemDetails` return value is the same instance as `WolverineContinue.NoProblems`, just keep going
2. Otherwise, write the `ProblemDetails` out to the HTTP response and exit the HTTP request handling

To make that clearer, here's the generated code:

```cs
public sealed class POST_problems : HttpHandler
{
    private readonly WolverineHttpOptions _wolverineHttpOptions;

    public POST_problems(WolverineHttpOptions wolverineHttpOptions) : base(wolverineHttpOptions)
    {
        _wolverineHttpOptions = wolverineHttpOptions;
    }

    public override async Task Handle(HttpContext httpContext)
    {
        var problemDetailsUsageEndpoint = new ProblemDetailsUsageEndpoint();
        // (Open Telemetry tagging omitted)
        // Reading the request body via JSON deserialization
        var (message, jsonContinue) = await ReadJsonAsync<NumberMessage>(httpContext);
        if (jsonContinue == HandlerContinuation.Stop) return;
        var problemDetails1 = problemDetailsUsageEndpoint.Validate(message);
        // Evaluate whether the processing should stop if there are any problems
        if (!(ReferenceEquals(problemDetails1, WolverineContinue.NoProblems)))
        {
            await WriteProblems(problemDetails1, httpContext).ConfigureAwait(false);
            return;
        }

        
        // The actual HTTP request handler execution
        var result_of_Post = ProblemDetailsUsageEndpoint.Post(message);

        await WriteString(httpContext, result_of_Post, 404);
    }
}
```

::: tip
That's what Wolverine 7.0 generates for this endpoint today, lightly trimmed. To see the real thing for your own endpoints, run `dotnet run -- codegen preview`.
:::

And for more context, here's the matching "happy path" and "sad path" tests for the endpoint above:

<!-- snippet: sample_testing_problem_details_behavior -->
<a id='snippet-sample_testing_problem_details_behavior'></a>
```cs
[Fact]
public async Task continue_happy_path()
{
    // Should be good
    await Scenario(x =>
    {
        x.Post.Json(new NumberMessage(3)).ToUrl("/problems");
    });
}

[Fact]
public async Task stop_with_problems_if_middleware_trips_off()
{
    // This is the "sad path" that should spawn a ProblemDetails
    // object
    var result = await Scenario(x =>
    {
        x.Post.Json(new NumberMessage(10)).ToUrl("/problems");
        x.StatusCodeShouldBe(400);
        x.ContentTypeShouldBe("application/problem+json");
    });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/problem_details_usage_in_http_middleware.cs#L18-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_testing_problem_details_behavior' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Lastly, if Wolverine sees the existence of a `ProblemDetails` return value in any middleware, Wolverine will fill in OpenAPI
metadata for the "application/problem+json" content type and a status code of 400. This behavior can be easily overridden
with your own metadata if you need to use a different status code like this:

```csharp
    // Use 418 as the status code instead
    [ProducesResponseType(typeof(ProblemDetails), 418)]
```

### Using ProblemDetails with Marten aggregates

Of course, if you are using [Marten's aggregates within your Wolverine http handlers](./marten), you also want to be able to validation using the aggregate's details in your middleware and this is perfectly possible like this:

<!-- snippet: sample_using_before_on_http_aggregate -->
<a id='snippet-sample_using_before_on_http_aggregate'></a>
```cs
[AggregateHandler]
public static ProblemDetails Before(IShipOrder command, Order order)
{
    if (order.IsShipped())
    {
        return new ProblemDetails
        {
            Detail = "Order already shipped",
            Status = 428
        };
    }
    return WolverineContinue.NoProblems;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L106-L120' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_before_on_http_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Using Lightweight Validation with String Messages

Instead of using `ProblemDetails` directly, you can also use one of the [Lightweight Validation](../handlers/index.md#lightweight-validation-with-string-messages) types: `IEnumerable<string>`, `string[]`, `Task<string[]>`, `ValueTask<string[]>` or `ValidationOutcome`. If the returned collection is not empty, Wolverine will create a `ProblemDetails` response with a 400 status code containing the validation messages.


<!-- snippet: sample_simple_validation_http_ienumerable -->
<a id='snippet-sample_simple_validation_http_ienumerable'></a>
```cs
public record SimpleValidateHttpEnumerableMessage(int Number);

public static class SimpleValidationHttpEnumerableEndpoint
{
    public static IEnumerable<string> Validate(SimpleValidateHttpEnumerableMessage message)
    {
        if (message.Number > 10)
        {
            yield return "Number must be 10 or less";
        }
    }

    [WolverinePost("/simple-validation/ienumerable")]
    public static string Post(SimpleValidateHttpEnumerableMessage message) => "Ok";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/SimpleValidationUsage.cs#L6-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simple_validation_http_ienumerable' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Within Message Handlers <Badge type="tip" text="3.0" />

`ProblemDetails` can be used within message handlers as well with similar rules. See this example
from the tests:

<!-- snippet: sample_using_problem_details_in_message_handler -->
<a id='snippet-sample_using_problem_details_in_message_handler'></a>
```cs
public static class NumberMessageHandler
{
    public static ProblemDetails Validate(NumberMessage message)
    {
        if (message.Number > 5)
        {
            return new ProblemDetails
            {
                Detail = "Number is bigger than 5",
                Status = 400
            };
        }
        
        // All good, keep on going!
        return WolverineContinue.NoProblems;
    }

    // This "Before" method would only be utilized as
    // an HTTP endpoint
    [WolverineBefore(MiddlewareScoping.HttpEndpoints)]
    public static void BeforeButOnlyOnHttp(HttpContext context)
    {
        Debug.WriteLine("Got an HTTP request for " + context.TraceIdentifier);
        CalledBeforeOnlyOnHttpEndpoints = true;
    }

    // This "Before" method would only be utilized as
    // a message handler
    [WolverineBefore(MiddlewareScoping.MessageHandlers)]
    public static void BeforeButOnlyOnMessageHandlers()
    {
        CalledBeforeOnlyOnMessageHandlers = true;
    }

    // Look at this! You can use this as an HTTP endpoint too!
    [WolverinePost("/problems2")]
    public static void Handle(NumberMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }

    // These properties are just a cheap trick in Wolverine internal tests
    public static bool Handled { get; set; }
    public static bool CalledBeforeOnlyOnMessageHandlers { get; set; }
    public static bool CalledBeforeOnlyOnHttpEndpoints { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/ProblemDetailsUsage.cs#L37-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_problem_details_in_message_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This functionality was added so that some handlers could be both an endpoint and message handler
without having to duplicate code or delegate to the handler through an endpoint. 
