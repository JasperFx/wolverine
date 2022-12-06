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

public static class AccountMiddleware
{
    // TODO -- add ILogger here too
    // TODO -- the message *has* to be first in the parameter list
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
    [Transactional]
    public static void Handle(DebitAccount command, Account account, IDocumentSession session)
    {
        account.Balance += command.Amount;
        session.Store(account);
    }
}