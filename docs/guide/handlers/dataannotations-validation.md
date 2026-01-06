# DataAnnotations Validation Middleware


::: tip
There is also an HTTP specific middleware for WolverineFx.Http that uses the `ProblemDetails` specification. See
[DataAnnotations Validation Middleware for HTTP](/guide/http/dataannotationsvalidation) for more information.
:::

::: warning
While it is possible to access the IoC Services via `ValidationContext`, we recommend instead using a
more explicit `Validate` or `ValidateAsync()` method directly in your message handler class for the data input.
:::

For simple input validation of your messages, the [Data Annotation Attributes](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.dataannotations?view=net-10.0)
are a good choice. The `WolverineFx.DataAnnotationsValidation` nuget package will add support
for the built-in and custom attributes via middleware that will stop invalid messages from
reaching the message handlers.

To get started, add the nuget package and configure your Wolverine Application:

<!-- snippet: sample_bootstrap_with_dataannotations_validation -->
<a id='snippet-sample_bootstrap_with_dataannotations_validation'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Apply the validation middleware
        opts.UseDataAnnotationsValidation();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.DataAnnotationsValidation.Tests/Samples.cs#L13-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrap_with_dataannotations_validation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Now you can decorate your messages with the built-in or custom `ValidationAttributes`:

<!-- snippet: dataannotations_usage -->
<a id='snippet-dataannotations_usage'></a>
```cs
public record CreateCustomer(
    // you can use the attributes on a record, but you need to
    // add the `property` modifier to the attribute
    [property: Required] string FirstName,
    [property: MinLength(5)] string LastName,
    [property: PostalCodeValidator] string PostalCode
) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // you can implement `IValidatableObject` for custom
        // validation logic
        yield break;
    }
};

public class PostalCodeValidatorAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        // custom attributes are supported
        return true;
    }
}

public static class CreateCustomerHandler
{
    public static void Handle(CreateCustomer customer)
    {
        // do whatever you'd do here, but this won't be called
        // at all if the DataAnnotations Validation rules fail
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.DataAnnotationsValidation.Tests/Samples.cs#L64-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-dataannotations_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case above, the Validation check will happen at runtime *before* the call to the handler methods. If 
the validation fails, the middleware will throw a `ValidationException` and stop all processing.

Some notes about the middleware:

* The middleware is applied to all message handler types as there is no easy way of knowing if a message
  has some sort of validation attribute defined.
* The registration also adds an error handling policy to discard messages when a `ValidationException` is thrown

## Customizing the Validation Failure Behavior

Out of the box, the Fluent Validation middleware will throw a `DataAnnotationsValidation.ValidationException`
with all the validation failures if the validation fails. To customize that behavior, you can plug
in a custom implementation of the `IFailureAction<T>` interface. This behaves exactly the same as 
the [Fluent Validation Customisation](/guide/handlers/fluent-validation).
