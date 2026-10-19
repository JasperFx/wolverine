using JasperFx.Events;
using JasperFx.Events.Documents;
using Wolverine.Http;

namespace BankAccountES;

/// <summary>
/// Read model: a transaction history per account, built from deposit and withdrawal events.
/// Replaces the Java sample's commutative TransactionProjection that used version-based ordering —
/// the event store's sequence numbers already guarantee the order, whichever store it is.
/// </summary>
/// <remarks>
/// This used to be a <c>SingleStreamProjection&lt;AccountTransactions, Guid&gt;</c> subclass, which
/// is a <i>Marten</i> base class (Fisher and Polecat each have their own). A self-aggregating document
/// registered with <c>Snapshot&lt;AccountTransactions&gt;(SnapshotLifecycle.Inline)</c> is the one
/// shape of projection every store spells identically, so the read model moved its <c>Create</c> /
/// <c>Apply</c> conventions onto itself and the projection class is gone. Same generator, same
/// dispatch, no store in the signature. The event timestamp comes from <see cref="IEvent{T}"/>
/// metadata rather than the clock, so a rebuilt read model carries the original times.
/// </remarks>
public class AccountTransactions
{
    public Guid Id { get; set; } // AccountId
    public List<Transaction> Transactions { get; set; } = [];
    public decimal Balance { get; set; }

    public static AccountTransactions Create(AccountOpened e) => new() { Id = e.AccountId };

    public void Apply(IEvent<FundsDeposited> e)
    {
        Balance = e.Data.NewBalance;
        Transactions.Add(new Transaction
        {
            Type = "Deposit",
            Amount = e.Data.Amount,
            BalanceAfter = e.Data.NewBalance,
            Timestamp = e.Timestamp,
        });
    }

    public void Apply(IEvent<FundsWithdrawn> e)
    {
        Balance = e.Data.NewBalance;
        Transactions.Add(new Transaction
        {
            Type = "Withdrawal",
            Amount = e.Data.Amount,
            BalanceAfter = e.Data.NewBalance,
            Timestamp = e.Timestamp,
        });
    }
}

public class Transaction
{
    public string Type { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

// --- Query endpoints ---
//
// Reads go through the JasperFx.Events contracts Wolverine injects for every store:
// IDocumentReadOperations for documents (the inline snapshots), IEventStoreOperations for the
// stream itself. Marten's IQuerySession and Fisher's both implement them, and Wolverine resolves
// either to the current session, so these endpoints have no `using Marten` and no `using Fisher`.

public static class GetTransactionsEndpoint
{
    [WolverineGet("/api/accounts/{accountId}/transactions")]
    public static Task<AccountTransactions?> Get(Guid accountId, IDocumentReadOperations reads, CancellationToken ct)
        => reads.LoadAsync<AccountTransactions>(accountId, ct);
}

public static class GetAccountEndpoint
{
    // Rebuilt from the stream on every read — the point of the sample is that the aggregate IS its
    // events; the inline snapshot is there for [DeciderFunction] and [Entity] to load cheaply.
    [WolverineGet("/api/accounts/{id}")]
    public static Task<Account?> Get(Guid id, IEventStoreOperations events, CancellationToken ct)
        => events.AggregateStreamAsync<Account>(id, token: ct);
}

public static class GetClientEndpoint
{
    [WolverineGet("/api/clients/{id}")]
    public static Task<Client?> Get(Guid id, IEventStoreOperations events, CancellationToken ct)
        => events.AggregateStreamAsync<Client>(id, token: ct);
}

public static class GetClientAccountsEndpoint
{
    // LINQ over the Account snapshots, through the store-agnostic IQueryable and JasperFx.Events'
    // own ToListAsync — Marten's LINQ on Postgres, Fisher's on SQLite, same line.
    [WolverineGet("/api/clients/{clientId}/accounts")]
    public static Task<IReadOnlyList<Account>> Get(Guid clientId, IDocumentReadOperations reads, CancellationToken ct)
        => reads.Query<Account>().Where(a => a.ClientId == clientId).ToListAsync(ct);
}
