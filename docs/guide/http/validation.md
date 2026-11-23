# Validation within Wolverine.HTTP

::: info
You can of course use completely custom Wolverine middleware for validation, and once again, returning the `ProblemDetails`
object or `WolverineContinue.NoProblems` to communicate validation errors is our main recommendation in that case. 
:::

Wolverine.HTTP has direct support for utilizing validation within HTTP endpoint that all revolve around the
[ProblemDetails](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.problemdetails) specification.

1. Using one off `Validate()` or `ValidateAsync()` methods embedded directly in your endpoint types that return `ProblemDetails`. This is our recommendation for any 
   validation logic like data lookups that would require you to utilize IoC services or database calls.  
   The [Lightweight Validation with String Messages](../handlers/index.md#lightweight-validation-with-string-messages) will also generate `ProblemDetails` with status 400 for HTTP endpoints.
2. Fluent Validation middleware through the separate `WolverineFx.Http.FluentValidation` Nuget
3. [Data Annotations](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.dataannotations?view=net-10.0) middleware that is an option you have to explicitly configure within Wolverine.HTTP application

::: tip
We **very strongly** recommend using the one off `ValidateAsync()` method for any validation that requires you to use an IoC
service rather than trying to use the Fluent Validation `IValidator` interface. Especially if that validation logic
is specific to that HTTP endpoint.
:::

## Validate Methods and ProblemDetails

Returning `ProblemDetails` (or `WolverineContinue.NoProblems`) from a `Validate()` or `ValidateAsync()` method on your
endpoint, returning string messages instead and letting Wolverine turn them into a 400 `ProblemDetails`, and doing the
same thing in message handlers are all covered in [Problem Details](./problemdetails).

## Data Annotations <Badge type="tip" text="5.9" />

::: warning
While it is possible to access the IoC Services via `ValidationContext`, we recommend instead using a
more explicit `Validate` or `ValidateAsync()` method directly in your message handler class for the data input.
:::

Wolverine.Http ships with built-in middleware to use [Data Annotation Attributes](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.dataannotations?view=net-10.0)
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

### Async validation on .NET 11

On .NET 11 the middleware also picks up DataAnnotations' asynchronous rules: a request type with an
`AsyncValidationAttribute` (on the type or a property), or one that implements `IAsyncValidatableObject`, is validated
with `Validator.TryValidateObjectAsync`, so the async members run (`IsValidAsync`, `ValidateAsync`) with the request's
`RequestAborted` token instead of their synchronous fallbacks. Request types without async rules keep the synchronous
path. Error messages go through `ValidationAttribute.FormatErrorMessage`, so an attribute that overrides .NET 11's
`FormatMessage` hook shows its own wording in the `ProblemDetails`.

```csharp
public class NotTakenAttribute : AsyncValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) => ValidationResult.Success;

    protected override async Task<ValidationResult?> IsValidAsync(object? value, ValidationContext context,
        CancellationToken cancellationToken)
    {
        var users = context.GetRequiredService<IUserDirectory>();
        return await users.IsTakenAsync((string)value!, cancellationToken)
            ? new ValidationResult($"'{value}' is already taken", [context.MemberName!])
            : ValidationResult.Success;
    }
}
```

For an example, consider this input model that will be a request type in your application:

<!-- snippet: sample_validated_createaccount -->
<a id='snippet-sample_validated_createaccount'></a>
```cs
public record CreateAccount(
    // don't forget the property prefix on records
    [property: Required] string AccountName,
    [property: Reference] string Reference
) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AccountName.Equals("invalid", StringComparison.InvariantCultureIgnoreCase))
        {
            yield return new("AccountName is invalid", [nameof(AccountName)]);
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Validation/DataAnnotationsValidationEndpoints.cs#L17-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_validated_createaccount' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As long as the Data Annotations middleware is active, the `CreateAccount` model would be validated if used 
as the request body like this:

<!-- snippet: sample_posting_createaccount -->
<a id='snippet-sample_posting_createaccount'></a>
```cs
[WolverinePost("/validate/account")]
public static string Post(
    
    // In this case CreateAccount is being posted
    // as JSON
    CreateAccount account)
{
    return "Got a new account";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Validation/DataAnnotationsValidationEndpoints.cs#L37-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_posting_createaccount' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

or even like this:

<!-- snippet: sample_posting_create_account_as_query_string -->
<a id='snippet-sample_posting_create_account_as_query_string'></a>
```cs
[WolverinePost("/validate/account2")]
public static string Post2([FromQuery] CreateAccount customer)
{
    return "Got a new account";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Validation/DataAnnotationsValidationEndpoints.cs#L50-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_posting_create_account_as_query_string' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Fluent Validation Middleware

::: warning
If you need to use IoC services in a Fluent Validation `IValidator` that might force Wolverine to use a service locator
pattern in the generated code (basically from `AddScoped<T>(s => build it at runtime)`), we recommend instead using a
more explicit `Validate` or `ValidateAsync()` method directly in your HTTP endpoint class for the data input.
:::

::: warning
If you are using `ExtensionDiscovery.ManualOnly`, you must explicitly call `opts.UseFluentValidationProblemDetail()` in
your Wolverine configuration in addition to `opts.UseFluentValidation()`. Without this, the `IProblemDetailSource<T>`
service will not be registered and the middleware will fail at runtime. With the default `ExtensionDiscovery.Automatic`
mode, these services are registered automatically by the `WolverineFx.Http.FluentValidation` extension.

```csharp
services.AddWolverine(ExtensionDiscovery.ManualOnly, opts =>
{
    opts.UseFluentValidation();
    opts.UseFluentValidationProblemDetail(); // Required in manual discovery mode!
});
```
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

::: tip AOT publishing
`UseFluentValidation()` defaults to `RegistrationBehavior.DiscoverAndRegisterValidators`, which runs FluentValidation's
`AssemblyScanner` at bootstrap and is **not trim/AOT-safe**. For AOT publishing, opt out of the scan and register your
validators explicitly:

```csharp
opts.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);
opts.Services.AddScoped<IValidator<CreateCustomer>, CreateCustomerValidator>();
```

See [Validation and AOT](/guide/aot.html#validation-and-aot) for the full story.
:::

From Wolverine 7.0 that is all it takes: when the `WolverineFx.Http.FluentValidation` package is referenced and
`UseFluentValidation()` is called, every Wolverine HTTP endpoint whose request type has a validator gets the
validation middleware, which answers `400` with a `ValidationProblemDetails` body when validation fails. In 6.x you also
had to add this line to your Wolverine.Http bootstrapping, and it still works, applying the middleware once however
often it is called:

```csharp
opts.UseFluentValidationProblemDetailMiddleware();
```

To keep the middleware off your HTTP endpoints, call `opts.DoNotUseFluentValidationProblemDetailMiddleware()` in
`MapWolverineEndpoints()`. `opts.RestoreV6Defaults()` in `UseWolverine()` turns the automatic middleware off too, and an
explicit `UseFluentValidationProblemDetailMiddleware()` still applies it after either. With
`ExtensionDiscovery.ManualOnly`, the package is not loaded automatically; call `opts.UseFluentValidationProblemDetail()`
in `UseWolverine()` to load it.

The explicit call is shown in context below in an application:

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

    // Or instead, you could use Data Annotations that are built
    // into the Wolverine.HTTP library
    opts.UseDataAnnotationsValidationProblemDetailMiddleware();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L304-L328' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_configure_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## AsParameters Binding

The Fluent Validation middleware can also be used against the `[AsParameters]` input
of an HTTP endpoint:

<!-- snippet: sample_using_fluent_validation_with_asparameters -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Forms/FormEndpoints.cs#L232-L258' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_fluent_validation_with_asparameters' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### AsParameters with [FromBody] <Badge type="tip" text="5.25" />

When using `[AsParameters]` with a `[FromBody]` property, the Fluent Validation middleware will validate
**both** the `[FromBody]` type (if it has a validator) and the `[AsParameters]` type itself. This ensures
that validation rules on the `[AsParameters]` type are always applied, even when a `[FromBody]` property
is present:

<!-- snippet: sample_using_fluent_validation_with_asparameters_and_frombody -->
<a id='snippet-sample_using_fluent_validation_with_asparameters_and_frombody'></a>
```cs
public static class ValidatedAsParametersWithFromBodyEndpoint
{
    [WolverinePost("/asparameters/validated_with_from_body")]
    public static string Post([AsParameters] ValidatedWithFromBody query)
    {
        return $"{query.Name} has dog: {query.Body?.HasDog}, has cat: {query.Body?.HasCat}";
    }
}

public class ValidatedWithFromBody
{
    [FromQuery]
    public string? Name { get; set; }

    [FromQuery]
    public int Age { get; set; }

    [FromBody]
    public ValidatedQueryBody? Body { get; set; }

    public class ValidatedWithFromBodyValidator : AbstractValidator<ValidatedWithFromBody>
    {
        public ValidatedWithFromBodyValidator()
        {
            RuleFor(x => x.Name).NotNull();
            RuleFor(x => x.Body).NotNull();
        }
    }

    public class ValidatedQueryBody
    {
        public bool HasDog { get; set; }
        public bool HasCat { get; set; }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Forms/FormEndpoints.cs#L260-L297' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_fluent_validation_with_asparameters_and_frombody' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## QueryString Binding <Badge type="tip" text="5.0" />

Wolverine.HTTP can apply the Fluent Validation middleware to complex types that are bound by the `[FromQuery]` behavior:

<!-- snippet: sample_createcustomer_endpoint_with_validation -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Validation/CreateCustomerEndpoint.cs#L8-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_createcustomer_endpoint_with_validation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
