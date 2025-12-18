# Validation within Wolverine.HTTP

::: info
You can of course use completely custom Wolverine middleware for validation, and once again, returning the `ProblemDetails`
object or `WolverineContinue.NoProblems` to communicate validation errors is our main recommendation in that case. 
:::

Wolverine.HTTP has direct support for utilizing validation within HTTP endpoint that all revolve around the
[ProblemDetails](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.problemdetails?view=aspnetcore-7.0) specification.

1. Using one off `Validate()` or `ValidateAsync()` methods embedded directly in your endpoint types that return `ProblemDetails`. This is our recommendation for any 
   validation logic like data lookups that would require you to utilize IoC services or database calls.
2. Fluent Validation middleware through the separate `WolverineFx.Http.FluentValidation` Nuget
3. [Data Annotations](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.dataannotations?view=net-10.0) middleware that is an option you have to explicitly configure within Wolverine.HTTP application

::: tip
We **very strongly** recommend using the one off `ValidateAsync()` method for any validation that requires you to use an IoC'
service rather than trying to use the Fluent Validation `IValidator` interface. Especially if that validation logic
is specific to that HTTP endpoint.
:::

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/ProblemDetailsUsage.cs#L8-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_problemdetailsusageendpoint' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L103-L117' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_before_on_http_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## ProblemDetails Within Message Handlers <Badge type="tip" text="3.0" />

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/ProblemDetailsUsage.cs#L38-L88' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_problem_details_in_message_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This functionality was added so that some handlers could be both an endpoint and message handler
without having to duplicate code or delegate to the handler through an endpoint.


## Data Annotations <Badge type="tip" text="5.9" />

::: warning
While it is possible to access the IoC Services via `ValidationContext`, we recommend instead using a
more explicit `Validate` or `ValidateAsync()` method directly in your message handler class for the data input.
:::

Wolverine.Http has a separate package called `WolverineFx.Http.DataAnnotationsValidation` that provides a simple middleware
to use  [Data Annotation Attributes](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.dataannotations?view=net-10.0)
in your endpoints.

To get started, add this one line of code to your Wolverine.HTTP configuration:

```csharp
app.MapWolverineEndpoints(opts =>
{
    // Use Data Annotations that are built
    // into the Wolverine.HTTP library
    opts.UseDataAnnotationsValidationProblemDetailMiddleware();

});
```

This middleware will kick in for any HTTP endpoint where the request type has any property
decorated with a [`ValidationAttribute`](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.dataannotations.validationattribute?view=net-10.0)
or which implements the `IValidatableObject` interface.

Any validation errors detected will cause the HTTP request to fail with a `ProblemDetails` response.

For an example, consider this input model that will be a request type in your application:

snippet: sample_validated_CreateAccount

As long as the Data Annotations middleware is active, the `CreateAccount` model would be validated if used 
as the request body like this:

snippet: sample_posting_CreateAccount

or even like this:

snippet: sample_posting_create_account_as_query_string

## Fluent Validation Middleware

::: warning
If you need to use IoC services in a Fluent Validation `IValidator` that might force Wolverine to use a service locator
pattern in the generated code (basically from `AddScoped<T>(s => build it at runtime)`), we recommend instead using a
more explicit `Validate` or `ValidateAsync()` method directly in your HTTP endpoint class for the data input.
:::

Wolverine.Http has a separate package called `WolverineFx.Http.FluentValidation` that provides a simple middleware
for using [Fluent Validation](https://docs.fluentvalidation.net/en/latest/) in your HTTP endpoints.

To get started, install that Nuget reference:

```bash
dotnet add package WolverineFx.Http.FluentValidation
```

Next, let's assume that you have some Fluent Validation validators registered in your application container for the
request types of your HTTP endpoints -- and the [UseFluentValidation](/guide/handlers/fluent-validation) method from the
`WolverineFx.FluentValidation` package will help find these validators and register them in a way that optimizes this
middleware usage.

Next, add this one single line of code to your Wolverine.Http bootstrapping:

```csharp
opts.UseFluentValidationProblemDetailMiddleware();
```

as shown in context below in an application shown below:

<!-- snippet: sample_using_configure_endpoints -->
<a id='snippet-sample_using_configure_endpoints'></a>
```cs
app.MapWolverineEndpoints(opts =>
{
    // This is strictly to test the endpoint policy

    opts.ConfigureEndpoints(httpChain =>
    {
        // The HttpChain model is a configuration time
        // model of how the HTTP endpoint handles requests

        // This adds metadata for OpenAPI
        httpChain.WithMetadata(new CustomMetadata());
    });

    // more configuration for HTTP...

    // Opting into the Fluent Validation middleware from
    // Wolverine.Http.FluentValidation
    opts.UseFluentValidationProblemDetailMiddleware();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L214-L235' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_configure_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## AsParameters Binding

The Fluent Validation middleware can also be used against the `[AsParameters]` input
of an HTTP endpoint:

<!-- snippet: sample_using_fluent_validation_with_AsParameters -->
<a id='snippet-sample_using_fluent_validation_with_asparameters'></a>
```cs
public static class ValidatedAsParametersEndpoint
{
    [WolverineGet("/asparameters/validated")]
    public static string Get([AsParameters] ValidatedQuery query)
    {
        return $"{query.Name} is {query.Age}";
    }
}

public class ValidatedQuery
{
    [FromQuery]
    public string? Name { get; set; }
    
    public int Age { get; set; }

    public class ValidatedQueryValidator : AbstractValidator<ValidatedQuery>
    {
        public ValidatedQueryValidator()
        {
            RuleFor(x => x.Name).NotNull();
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Forms/FormEndpoints.cs#L200-L227' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_fluent_validation_with_asparameters' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## QueryString Binding <Badge type="tip" text="5.0" />

Wolverine.HTTP can apply the Fluent Validation middleware to complex types that are bound by the `[FromQuery]` behavior:

<!-- snippet: sample_CreateCustomer_endpoint_with_validation -->
<a id='snippet-sample_createcustomer_endpoint_with_validation'></a>
```cs
public record CreateCustomer
(
    string FirstName,
    string LastName,
    string PostalCode
)
{
    public class CreateCustomerValidator : AbstractValidator<CreateCustomer>
    {
        public CreateCustomerValidator()
        {
            RuleFor(x => x.FirstName).NotNull();
            RuleFor(x => x.LastName).NotNull();
            RuleFor(x => x.PostalCode).NotNull();
        }
    }
}

public static class CreateCustomerEndpoint
{
    [WolverinePost("/validate/customer")]
    public static string Post(CreateCustomer customer)
    {
        return "Got a new customer";
    }
    
    [WolverinePost("/validate/customer2")]
    public static string Post2([FromQuery] CreateCustomer customer)
    {
        return "Got a new customer";
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Validation/CreateCustomerEndpoint.cs#L8-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_createcustomer_endpoint_with_validation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
