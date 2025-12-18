using System.ComponentModel.DataAnnotations;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.DataAnnotationsValidation.Tests;

public class Samples
{
    [Fact]
    public async Task register_the_middleware()
    {
        #region sample_bootstrap_with_dataannotations_validation

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Apply the validation middleware
                opts.UseDataAnnotationsValidation();
            }).StartAsync();

        #endregion
    }

    [Fact]
    public async Task register_the_middleware_with_override_failure_condition()
    {
        #region sample_bootstrap_with_dataannotations_validation_and_custom_failure_condition

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Apply the validation middleware
                opts.UseDataAnnotationsValidation();

                // Override the service registration for IFailureAction
                opts.Services.AddSingleton(typeof(IFailureAction<>), typeof(CustomFailureAction<>));
                
            }).StartAsync();

        #endregion
    }
}

#region sample_customizing_dataannotations_validation_failure_actions

public class MySpecialException : Exception
{
    public MySpecialException(string? message) : base(message)
    {
    }
}

public class CustomFailureAction<T> : IFailureAction<T>
{
    public void Throw(T message, ICollection<ValidationResult> failures)
    {
        throw new MySpecialException("Your message stinks!: " + failures.Select(x => x.ErrorMessage!).Join(", "));
    }
}

#endregion

#region dataannotations_usage

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

#endregion