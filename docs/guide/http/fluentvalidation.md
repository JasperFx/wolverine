# Fluent Validation Middleware for HTTP

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L216-L237' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_configure_endpoints' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/FormEndpoints.cs#L201-L228' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_fluent_validation_with_asparameters' title='Start of snippet'>anchor</a></sup>
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
