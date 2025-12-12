namespace Wolverine.Configuration.Capabilities;

// This would be persisted
public class ServiceRegistration
{
    // surrogate key
    public Guid Id { get; set; }

    public string Name { get; set; } = "Critter Service";
    public string Description { get; set; }
    
    // This could be local?
    public Uri CritterWatchUri { get; set; } = new Uri("local://");
}