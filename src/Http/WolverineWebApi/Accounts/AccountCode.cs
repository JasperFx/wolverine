using JasperFx.Events;
using Marten.Events;
using Wolverine.Http;
using Wolverine.Http.Marten;

namespace WolverineWebApi.Accounts;

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

public record TransferMoney(Guid FromId, Guid ToId, double Amount);

public static class TransferMoneyEndpoint
{
    [WolverinePost("/accounts/transfer")]
    public static void Post(
        TransferMoney command,

        [Aggregate(nameof(TransferMoney.FromId))] IEventStream<Account> fromAccount,
        
        [Aggregate(nameof(TransferMoney.ToId))] IEventStream<Account> toAccount)
    {
        // Would already 404 if either referenced account does not exist
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}