## Using ProblemDetails

Wolverine has some first class support for the [ProblemDetails](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.problemdetails?view=aspnetcore-7.0) specification in its [HTTP middleware model](./middleware).
Wolverine also has a [Fluent Validation middleware package](./fluentvalidation) for HTTP endpoints, but it's frequently valuable to write one
off, explicit validation for certain endpoints. 

Consider this contrived sample endpoint with explicit validation being done in a "Before" middleware method:

<!-- snippet: sample_ProblemDetailsUsageEndpoint -->
<a id='snippet-sample_problemdetailsusageendpoint'></a>
```cs
public class ProblemDetailsUsageEndpoint
{
    public ProblemDetails Before(NumberMessage message)
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/ProblemDetailsUsage.cs#L6-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_problemdetailsusageendpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine.Http now (as of 1.2.0) has a convention that sees a return value of `ProblemDetails` and looks at that as a
"continuation" to tell the http handler code what to do next. One of two things will happen:

1. If the `ProblemDetails` return value is the same instance as `WolverineContinue.NoProblems`, just keep going
2. Otherwise, write the `ProblemDetails` out to the HTTP response and exit the HTTP request handling

To make that clearer, here's the generated code:

```csharp
    public class POST_problems : Wolverine.Http.HttpHandler
    {
        private readonly Wolverine.Http.WolverineHttpOptions _wolverineHttpOptions;

        public POST_problems(Wolverine.Http.WolverineHttpOptions wolverineHttpOptions) : base(wolverineHttpOptions)
        {
            _wolverineHttpOptions = wolverineHttpOptions;
        }

        public override async System.Threading.Tasks.Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            var problemDetailsUsageEndpoint = new WolverineWebApi.ProblemDetailsUsageEndpoint();
            var (message, jsonContinue) = await ReadJsonAsync<WolverineWebApi.NumberMessage>(httpContext);
            if (jsonContinue == Wolverine.HandlerContinuation.Stop) return;
            
            var problemDetails = problemDetailsUsageEndpoint.Before(message);
            if (!(ReferenceEquals(problemDetails, Wolverine.Http.WolverineContinue.NoProblems)))
            {
                await Microsoft.AspNetCore.Http.Results.Problem(problemDetails).ExecuteAsync(httpContext).ConfigureAwait(false);
                return;
            }

            var result_of_Post = WolverineWebApi.ProblemDetailsUsageEndpoint.Post(message);
            await WriteString(httpContext, result_of_Post);
        }

    }
```

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/problem_details_usage_in_http_middleware.cs#L18-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_testing_problem_details_behavior' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L87-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_before_on_http_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
