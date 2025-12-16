using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi.Validation;


#region sample_endpoint_with_dataannotations_validation

public class ReferenceAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        return value is string { Length: 8 };
    }
}

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

public static class CreateAccountEndpoint
{
    [WolverinePost("/validate/account")]
    public static string Post(CreateAccount account)
    {
        return "Got a new account";
    }

    [WolverinePost("/validate/account2")]
    public static string Post2([FromQuery] CreateAccount customer)
    {
        return "Got a new account";
    }
}

#endregion

public static class CreateAccountCompoundEndpoint
{
    public record Account(string Name);

    public static Account Load(CreateAccount cmd)
    {
        if (cmd.AccountName == null)
            throw new ApplicationException("Something went wrong. Fluent validation should stop execution before load");

        return new Account(cmd.AccountName);
    }

    public static ProblemDetails Validate(Account account)
    {
        if (account == null)
            throw new ApplicationException("Something went wrong. Fluent validation should stop execution before load");

        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/validate/account-compound")]
    public static string Post(CreateAccount cmd)
    {
        return "Got a new account";
    }
}