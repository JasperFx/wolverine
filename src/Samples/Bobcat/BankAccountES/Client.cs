namespace BankAccountES;

// --- Domain Events ---

public record ClientEnrolled(Guid ClientId, string Name, string Email);
public record ClientUpdated(Guid ClientId, string Name, string Email);

// --- Aggregate (event-sourced; the store is whichever Program.cs registered) ---

public class Client
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    public void Apply(ClientEnrolled e)
    {
        Id = e.ClientId;
        Name = e.Name;
        Email = e.Email;
    }

    public void Apply(ClientUpdated e)
    {
        Name = e.Name;
        Email = e.Email;
    }
}
