using Baseline;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.FluentValidation.Tests;

public class Samples
{
    [Fact]
    public async Task register_the_middleware()
    {
        #region sample_bootstrap_with_fluent_validation

        using var host = await  Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Apply the validation middleware *and* discover and register
                // Fluent Validation validators
                opts.UseFluentValidation();

                // Or if you'd prefer to deal with all the DI registrations yourself
                opts.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);
            }).StartAsync();

        #endregion
    }
    
    [Fact]
    public async Task register_the_middleware_with_override_failure_condition()
    {
        #region sample_bootstrap_with_fluent_validation_and_custom_failure_condition

        using var host = await  Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Apply the validation middleware *and* discover and register
                // Fluent Validation validators
                opts.UseFluentValidation();

                // Override the service registration for IFailureAction
                opts.Services.AddSingleton(typeof(IFailureAction<>), typeof(CustomFailureAction<>));
            }).StartAsync();

        #endregion
    }
}

#region sample_customizing_fluent_validation_failure_actions

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

#endregion


#region sample_create_customer

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

#endregion