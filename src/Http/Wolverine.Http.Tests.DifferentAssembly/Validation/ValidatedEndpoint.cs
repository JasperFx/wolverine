using FluentValidation;

namespace Wolverine.Http.Tests.DifferentAssembly.Validation;

public class Validated2Endpoint
{
    [WolverinePost("/validate2/customer")]
    public static string Post(CreateCustomer2 customer)
    {
        return "Got a new customer";
    }

    [WolverinePost("/validate2/user")]
    public static string Post(CreateUser2 user)
    {
        return "Got a new user";
    }
}

public record CreateCustomer2
(
    string FirstName,
    string LastName,
    string PostalCode
)
{
    public class CreateCustomerValidator : AbstractValidator<CreateCustomer2>
    {
        public CreateCustomerValidator()
        {
            RuleFor(x => x.FirstName).NotNull();
            RuleFor(x => x.LastName).NotNull();
            RuleFor(x => x.PostalCode).NotNull();
        }
    }
}

public record CreateUser2
(
    string FirstName,
    string LastName,
    string PostalCode,
    string Password
)
{
    public class CreateUserValidator : AbstractValidator<CreateUser2>
    {
        public CreateUserValidator()
        {
            RuleFor(x => x.FirstName).NotNull();
            RuleFor(x => x.LastName).NotNull();
            RuleFor(x => x.PostalCode).NotNull();
        }
    }

    public class PasswordValidator : AbstractValidator<CreateUser2>
    {
        public PasswordValidator()
        {
            RuleFor(x => x.Password).Length(8);
        }
    }
}