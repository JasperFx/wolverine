# Fluent Validation Middleware

::: warning
Wolverine's `UseFluentValidation()` does "type scanning" to discover validators unless you explicitly tell
Wolverine not to. Be careful to not double register validators through some other mechanism and Wolverine's. Do
note that Wolverine makes some performance optimizations around the `ServiceLifetime` of DI registrations
for validation that can be valuable in terms of performance.
:::

::: tip
There is also an HTTP specific middleware for WolverineFx.Http that uses the `ProblemDetails` specification. See
[Fluent Validation Middleware for HTTP](/guide/http/fluentvalidation) for more information.
:::

You will frequently want or need to validate the messages coming into your Wolverine system for correctness
or at least the presence of vital information. To that end, Wolverine has support for integrating the
popular [Fluent Validation](https://docs.fluentvalidation.net/en/latest/) library via an unobtrusive middleware strategy
where the middleware will stop invalid messages from even reaching the message handlers.

To get started, add the `WolverineFx.FluentValidation` nuget to your project, and add this line
to your Wolverine application bootstrapping:

<!-- snippet: sample_bootstrap_with_fluent_validation -->
<a id='snippet-sample_bootstrap_with_fluent_validation'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Apply the validation middleware *and* discover and register
        // Fluent Validation validators
        opts.UseFluentValidation();

        // Or if you'd prefer to deal with all the DI registrations yourself
        opts.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);

        // Just a prerequisite for some of the test validators
        opts.Services.AddSingleton<IDataService, DataService>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.FluentValidation.Tests/Samples.cs#L14-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrap_with_fluent_validation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And now to situate this within the greater application, let's say you have a message and handler
for creating a new customer, and you also have a Fluent Validation validator for your `CreateCustomer`
message type in your codebase:

<!-- snippet: sample_create_customer -->
<a id='snippet-sample_create_customer'></a>
```cs
public class CreateCustomerValidator : AbstractValidator<CreateCustomer>
{
    public CreateCustomerValidator()
    {
        RuleFor(x => x.FirstName).NotNull();
        RuleFor(x => x.LastName).NotNull();
        RuleFor(x => x.PostalCode).NotNull();
    }
}

public record CreateCustomer
(
    string FirstName,
    string LastName,
    string PostalCode
);

public static class CreateCustomerHandler
{
    public static void Handle(CreateCustomer customer)
    {
        // do whatever you'd do here, but this won't be called
        // at all if the Fluent Validation rules fail
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.FluentValidation.Tests/Samples.cs#L75-L103' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_create_customer' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case above, the Fluent Validation check will happen at runtime *before* the call to the handler methods. If 
the validation fails, the middleware will throw a `ValidationException` and stop all processing.

Some notes about the middleware:

* The middleware is not applied to any message handler type that has no known validators in the application's IoC container
* Wolverine uses a slightly different version of the middleware based on whether or not there is a single validator or multiple
  validators in the underlying IoC container
* The registration also adds an error handling policy to discard messages when a `ValidationException` is thrown

## Customizing the Validation Failure Behavior

::: tip
Unless there's a good reason not to, register your custom `IFailureAction<T>` as singleton scoped
for a performance optimization within the Wolverine pipeline.
:::

Out of the box, the Fluent Validation middleware will throw a `FluentValidation.ValidationException`
with all the validation failures if the validation fails. To customize that behavior, you can plug
in a custom implementation of the `IFailureAction<T>` interface as shown below:

<!-- snippet: sample_customizing_fluent_validation_failure_actions -->
<a id='snippet-sample_customizing_fluent_validation_failure_actions'></a>
```cs
public class MySpecialException : Exception
{
    public MySpecialException(string? message) : base(message)
    {
    }
}

public class CustomFailureAction<T> : IFailureAction<T>
{
    public void Throw(T message, IReadOnlyList<ValidationFailure> failures)
    {
        throw new MySpecialException("Your message stinks!: " + failures.Select(x => x.ErrorMessage).Join(", "));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.FluentValidation.Tests/Samples.cs#L56-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customizing_fluent_validation_failure_actions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and with the corresponding override:

<!-- snippet: sample_bootstrap_with_fluent_validation_and_custom_failure_condition -->
<a id='snippet-sample_bootstrap_with_fluent_validation_and_custom_failure_condition'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Apply the validation middleware *and* discover and register
        // Fluent Validation validators
        opts.UseFluentValidation();

        // Override the service registration for IFailureAction
        opts.Services.AddSingleton(typeof(IFailureAction<>), typeof(CustomFailureAction<>));
        
        // Just a prerequisite for some of the test validators
        opts.Services.AddSingleton<IDataService, DataService>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.FluentValidation.Tests/Samples.cs#L36-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrap_with_fluent_validation_and_custom_failure_condition' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
