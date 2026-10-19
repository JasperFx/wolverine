namespace Wallets;

public class Wallet
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public string Currency { get; set; } = "PLN";
    public decimal Balance { get; set; }
    public List<Transfer> Transfers { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public class Transfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Incoming, Outgoing
    public decimal Amount { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
