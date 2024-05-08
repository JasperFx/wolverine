using FluentValidation;
using Wolverine.Http;

namespace WolverineWebApi.Validation;

public class ValidatedEndpoint
{
    [WolverinePost("/validate/customer")]
    public static string Post(CreateCustomer customer)
    {
        return "Got a new customer";
    }

    [WolverinePost("/validate/user")]
    public static string Post(CreateUser user)
    {
        return "Got a new user";
    }
}

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

public record CreateUser
(
    string FirstName,
    string LastName,
    string PostalCode,
    string Password
)
{
    public class CreateUserValidator : AbstractValidator<CreateUser>
    {
        public CreateUserValidator()
        {
            RuleFor(x => x.FirstName).NotNull();
            RuleFor(x => x.LastName).NotNull();
            RuleFor(x => x.PostalCode).NotNull();
        }
    }

    public class PasswordValidator : AbstractValidator<CreateUser>
    {
        public PasswordValidator()
        {
            RuleFor(x => x.Password).Length(8);
        }
    }
}