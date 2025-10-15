using System.Diagnostics;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi.Validation;

#region sample_CreateCustomer_endpoint_with_validation

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

#endregion

public static class OtherValidatedEndpoint
{
    [WolverinePost("/validate/user")]
    public static string Post(CreateUser user)
    {
        return "Got a new user";
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