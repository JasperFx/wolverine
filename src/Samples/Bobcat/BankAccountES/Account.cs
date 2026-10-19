namespace BankAccountES;

// --- Domain Events ---

public record AccountOpened(Guid AccountId, Guid ClientId, string Currency);
public record FundsDeposited(Guid AccountId, decimal Amount, decimal NewBalance);
public record FundsWithdrawn(Guid AccountId, decimal Amount, decimal NewBalance);

// --- Aggregate (event-sourced; the store is whichever Program.cs registered) ---

/// <summary>
/// Bank account aggregate. The event store rebuilds state by calling the Apply methods
/// when loading from the event stream — Marten and Fisher share that convention through
/// JasperFx.Events. Business rules are enforced in Wolverine handler methods that return events.
/// </summary>
public class Account
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal Balance { get; set; }
    public bool IsFrozen { get; set; }

    public void Apply(AccountOpened e)
    {
        Id = e.AccountId;
        ClientId = e.ClientId;
        Currency = e.Currency;
    }

    public void Apply(FundsDeposited e)
    {
        Balance = e.NewBalance;
    }

    public void Apply(FundsWithdrawn e)
    {
        Balance = e.NewBalance;
    }

    public void Apply(AccountFrozen e)
    {
        IsFrozen = true;
    }

    // AccountFlagged deliberately has no Apply — it is an audit marker, and aggregation skips
    // events no Apply claims. (It is also bobcat#172's planted disagreement; see FreezeAccount.cs.)
}
