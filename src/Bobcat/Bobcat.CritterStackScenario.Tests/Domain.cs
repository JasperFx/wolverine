namespace Bobcat.CritterStack.ScenarioTests;

// The domain the store-only vocabulary is specified against. Nothing here knows a store or a message bus:
// the hosts register it on Marten and on Fisher, and every act is a delegate over the host's services.

public record WalletOpened(Guid WalletId, string Owner);

public record WalletCredited(Guid WalletId, decimal Amount);

public record WalletDebited(Guid WalletId, decimal Amount);

/// <summary>A strong-typed id around the Guid a stream is identified by (wolverine#4865).</summary>
public readonly record struct WalletId(Guid Value);

/// <summary>The write model: an inline snapshot, folded from its stream.</summary>
public class Wallet
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = "";
    public decimal Balance { get; set; }

    public void Apply(WalletOpened e)
    {
        Id = e.WalletId;
        Owner = e.Owner;
    }

    public void Apply(WalletCredited e) => Balance += e.Amount;
    public void Apply(WalletDebited e) => Balance -= e.Amount;
}

/// <summary>An async-projected read model, so the projection waits have a daemon to wait on.</summary>
public class WalletSummary
{
    public Guid Id { get; set; }
    public int Credits { get; set; }
    public decimal Balance { get; set; }

    public void Apply(WalletOpened e) => Id = e.WalletId;

    public void Apply(WalletCredited e)
    {
        Credits++;
        Balance += e.Amount;
    }

    public void Apply(WalletDebited e) => Balance -= e.Amount;
}

/// <summary>A plain document, stored directly — the read-model arrange and the document reads.</summary>
public class Customer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
}

/// <summary>A document type nothing ever stores, for "there is no T at all".</summary>
public class Ghost
{
    public Guid Id { get; set; }
}

// ---- Dynamic Consistency Boundary tags (wolverine#4865) ---------------------------------------

public readonly record struct ScreeningId(Guid Value);

public record ScreeningScheduled(ScreeningId Screening, int Seats);

public record SeatReserved(ScreeningId Screening, string Seat);

public class SeatAvailability
{
    public Guid Id { get; set; }
    public int Seats { get; set; }

    public void Apply(ScreeningScheduled e) => Seats = e.Seats;
    public void Apply(SeatReserved e) => Seats--;
}

/// <summary>A refusal an application throws, wrapped by its plumbing before the act sees it.</summary>
public class WalletFrozen(Guid walletId) : Exception($"The wallet {walletId} is frozen")
{
    public Guid WalletId { get; } = walletId;
}

// ---- an ancillary store's side of the domain (bobcat#504) ---------------------------------------

public record InvoiceRaised(Guid InvoiceId, decimal Amount);

public record InvoicePaid(Guid InvoiceId);

/// <summary>The ancillary store's write model: an inline snapshot there, registered nowhere else.</summary>
public class Invoice
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public bool Paid { get; set; }

    public void Apply(InvoiceRaised e)
    {
        Id = e.InvoiceId;
        Amount = e.Amount;
    }

    public void Apply(InvoicePaid e) => Paid = true;
}

/// <summary>The ancillary store's async read model, so a wait has a second daemon to cover.</summary>
public class InvoiceSummary
{
    public Guid Id { get; set; }
    public decimal Raised { get; set; }
    public bool Paid { get; set; }

    public void Apply(InvoiceRaised e)
    {
        Id = e.InvoiceId;
        Raised += e.Amount;
    }

    public void Apply(InvoicePaid e) => Paid = true;
}

/// <summary>A document both stores register, so a step about it cannot be routed by type.</summary>
public class SharedNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>A document no store registers, and nothing ever stores.</summary>
public class Unclaimed
{
    public Guid Id { get; set; }
}

/// <summary>A document no store registers until the default store is handed one.</summary>
public class Stray
{
    public Guid Id { get; set; }
}
