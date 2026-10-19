using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Persistence.EventSourcing;

namespace BankAccountES;

public record WithdrawFunds(Guid AccountId, decimal Amount)
{
    public class Validator : AbstractValidator<WithdrawFunds>
    {
        public Validator()
        {
            RuleFor(x => x.AccountId).NotEmpty();
            RuleFor(x => x.Amount).GreaterThan(0);
        }
    }
}

public static class WithdrawFundsEndpoint
{
    // Sad path: insufficient funds check using the loaded aggregate
    public static ProblemDetails Validate(WithdrawFunds command, Account account)
    {
        if (account.Balance < command.Amount)
            return new ProblemDetails
            {
                Detail = $"Insufficient funds. Balance: {account.Balance:C}, requested: {command.Amount:C}",
                Status = 400,
            };

        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/api/accounts/{accountId}/withdrawals")]
    [DeciderFunction]
    public static (IResult, FundsWithdrawn) Post(WithdrawFunds command, Account account)
    {
        var newBalance = account.Balance - command.Amount;
        return (Results.NoContent(), new FundsWithdrawn(command.AccountId, command.Amount, newBalance));
    }
}
