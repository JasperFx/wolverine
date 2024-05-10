using FluentValidation;
using JasperFx.Core;
using Marten;
using Wolverine;
using Wolverine.Attributes;

namespace AppWithMiddleware;

public class Account
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }
    public decimal MinimumThreshold { get; set; }
}

public static class Samples
{
    #region sample_common_scenario

    public static async Task Handle(DebitAccount command, IDocumentSession session, ILogger logger)
    {
        // Try to find a matching account for the incoming command
        var account = await session.LoadAsync<Account>(command.AccountId);
        if (account == null)
        {
            logger.LogInformation("Referenced account {AccountId} does not exist", command.AccountId);
            return;
        }

        // do the real processing
    }

    #endregion
}

#region sample_IAccountCommand

public interface IAccountCommand
{
    Guid AccountId { get; }
}

#endregion

#region sample_CreditAccount

public record CreditAccount(Guid AccountId, decimal Amount) : IAccountCommand;

#endregion

#region sample_CreditAccountHandler

public static class CreditAccountHandler
{
    public static void Handle(
        CreditAccount command,

        // Wouldn't it be nice to just have Wolverine "push"
        // the right account into this method?
        Account account,

        // Using Marten for persistence here
        IDocumentSession session)
    {
        account.Balance += command.Amount;

        // Just mark this account as needing to be updated
        // in the database
        session.Store(account);
    }
}

#endregion


#region sample_AccountLookupMiddleware

// This is *a* way to build middleware in Wolverine by basically just
// writing functions/methods. There's a naming convention that
// looks for Before/BeforeAsync or After/AfterAsync
public static class AccountLookupMiddleware
{
    // The message *has* to be first in the parameter list
    // Before or BeforeAsync tells Wolverine this method should be called before the actual action
    public static async Task<(HandlerContinuation, Account?)> LoadAsync(
        IAccountCommand command,
        ILogger logger,

        // This app is using Marten for persistence
        IDocumentSession session,

        CancellationToken cancellation)
    {
        var account = await session.LoadAsync<Account>(command.AccountId, cancellation);
        if (account == null)
        {
            logger.LogInformation("Unable to find an account for {AccountId}, aborting the requested operation", command.AccountId);
        }

        return (account == null ? HandlerContinuation.Stop : HandlerContinuation.Continue, account);
    }
}

#endregion


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
    #region sample_DebitAccountHandler_that_uses_IMessageContext

    [Transactional]
    public static async Task Handle(
        DebitAccount command,
        Account account,
        IDocumentSession session,
        IMessageContext messaging)
    {
        account.Balance -= command.Amount;

        // This just marks the account as changed, but
        // doesn't actually commit changes to the database
        // yet. That actually matters as I hopefully explain
        session.Store(account);

        // Conditionally trigger other, cascading messages
        if (account.Balance > 0 && account.Balance < account.MinimumThreshold)
        {
            await messaging.SendAsync(new LowBalanceDetected(account.Id));
        }
        else if (account.Balance < 0)
        {
            await messaging.SendAsync(new AccountOverdrawn(account.Id), new DeliveryOptions{DeliverWithin = 1.Hours()});

            // Give the customer 10 days to deal with the overdrawn account
            await messaging.ScheduleAsync(new EnforceAccountOverdrawnDeadline(account.Id), 10.Days());
        }

        // "messaging" is a Wolverine IMessageContext or IMessageBus service
        // Do the deliver within rule on individual messages
        await messaging.SendAsync(new AccountUpdated(account.Id, account.Balance),
            new DeliveryOptions { DeliverWithin = 5.Seconds() });
    }

    #endregion
}

#region sample_using_deliver_within_attribute

// The attribute directs Wolverine to send this message with
// a "deliver within 5 seconds, or discard" directive
[DeliverWithin(5)]
public record AccountUpdated(Guid AccountId, decimal Balance);

#endregion

public record LowBalanceDetected(Guid AccountId) : IAccountCommand;
public record AccountOverdrawn(Guid AccountId) : IAccountCommand;

public record EnforceAccountOverdrawnDeadline(Guid AccountId) : TimeoutMessage(10.Days()), IAccountCommand;