using FluentValidation;
using Marten;
using Wolverine;
using Wolverine.Attributes;

namespace AppWithMiddleware;

public class Account
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }
}

public interface IAccountCommand
{
    Guid AccountId { get; }
}

// This is *a* way to build middleware in Wolverine by basically just
// writing functions/methods
public static class AccountLookupMiddleware
{
    // TODO -- add ILogger here too
    // The message *has* to be first in the parameter list
    // Before or BeforeAsync tells Wolverine this method should be called before the actual action
    public static async Task<(HandlerContinuation, Account?)> BeforeAsync(IAccountCommand command, IDocumentSession session, CancellationToken cancellation)
    {
        var account = await session.LoadAsync<Account>(command.AccountId, cancellation);
        return (account == null ? HandlerContinuation.Stop : HandlerContinuation.Continue, account);
    }
}

public class DebitAccountValidator : AbstractValidator<DebitAccount>
{
    public DebitAccountValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0);
    }
}

public record DebitAccount(Guid AccountId, decimal Amount) : IAccountCommand;

public static class DebitAccountHandler
{
    // This explicitly adds the transactional middleware
    // The Fluent Validation middleware is applied because there's a validator
    // The Account argument is passed in by the AccountLookupMiddleware middleware
    [Transactional] // The 
    public static void Handle(DebitAccount command, Account account, IDocumentSession session)
    {
        account.Balance += command.Amount;
        session.Store(account);
    }
}