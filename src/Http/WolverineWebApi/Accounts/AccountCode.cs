using JasperFx.Events;
using Marten.Events;
using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace WolverineWebApi.Accounts;

#region sample_account_domain_code

public record AccountCreated(double InitialAmount);
public record Debited(double Amount);
public record Withdrawn(double Amount);

public class Account
{
    public Guid Id { get; set; }
    public double Amount { get; set; }

    public static Account Create(IEvent<AccountCreated> e)
        => new Account { Id = e.StreamId, Amount = e.Data.InitialAmount};

    public void Apply(Debited e) => Amount += e.Amount;
    public void Apply(Withdrawn e) => Amount -= e.Amount;
}

#endregion

#region sample_TransferMoney_command

public record TransferMoney(Guid FromId, Guid ToId, double Amount);

#endregion

#region sample_TransferMoneyEndpoint

public static class TransferMoneyHandler    
{
    [WolverinePost("/accounts/transfer")]
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId))] IEventStream<Account> fromAccount,
        
        [WriteAggregate(nameof(TransferMoney.ToId))] IEventStream<Account> toAccount)
    {
        // Would already 404 if either referenced account does not exist
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}

#endregion

public static class TransferMoneyHandler2   
{
    [WolverinePost("/accounts/transfer2")]
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId), LoadStyle = ConcurrencyStyle.Exclusive)] IEventStream<Account> fromAccount,
        
        [WriteAggregate(nameof(TransferMoney.ToId))] IEventStream<Account> toAccount)
    {
        // Would already 404 if either referenced account does not exist
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}

public record TransferMoney2(Guid FromId, Guid ToId, double Amount, int FromVersion);

public static class TransferMoneyHandler3   
{
    [WolverinePost("/accounts/transfer3")]
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId))] IEventStream<Account> fromAccount,
        
        [WriteAggregate(nameof(TransferMoney.ToId))] IEventStream<Account> toAccount)
    {
        // Would already 404 if either referenced account does not exist
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}


public static class TransferMoneyEndpointWithBefore
{
    public static void Before(Account fromAccount, Account toAccount)
    {
        From = fromAccount;
        To = toAccount;
    }

    public static Account To { get; set; }

    public static Account From { get; set; }

    [WolverinePost("/accounts/transfer4")]
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId), LoadStyle = ConcurrencyStyle.Exclusive)] IEventStream<Account> fromAccount,

        [WriteAggregate(nameof(TransferMoney.ToId))] IEventStream<Account> toAccount)
    {
        // Would already 404 if either referenced account does not exist
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}

public static class TransferMoneyEndpointWithBeforeAggregate
{
    public static void Before(Account fromAccount, Account toAccount)
    {
        From = fromAccount;
        To = toAccount;
    }

    public static Account To { get; set; }

    public static Account From { get; set; }

    [WolverinePost("/accounts/transfer5")]
    public static void Handle(
        TransferMoney command,

        [Aggregate(nameof(TransferMoney.FromId))] IEventStream<Account> fromAccount,

        [Aggregate(nameof(TransferMoney.ToId))] IEventStream<Account> toAccount)
    {
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}

public static class TransferMoneyEndpointWithBeforeMixed
{
    public static void Before(Account fromAccount, Account toAccount)
    {
        From = fromAccount;
        To = toAccount;
    }

    public static Account To { get; set; }

    public static Account From { get; set; }

    [WolverinePost("/accounts/transfer6")]
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId))] IEventStream<Account> fromAccount,

        [ReadAggregate(nameof(TransferMoney.ToId))] Account toAccount)
    {
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
        }
    }
}

