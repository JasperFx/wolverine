namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-4659. The inner failure reported when a tenant's write was refused by <see cref="TenantWriteBrake" />
/// rather than by the database itself — the store failed moments ago and is in its cool-down, so this write
/// never reached the network.
/// </summary>
/// <remarks>
/// It is deliberately a distinct type. An operator reading a log of these should be able to tell "your
/// database is refusing connections several hundred times a second" from "Wolverine is holding back while
/// it waits to probe again", and a `TimeoutException` repeated at speed reads as the former.
/// </remarks>
public class TenantDatabaseBrakedException : Exception
{
    public TenantDatabaseBrakedException(string? tenantId)
        : base(
            $"The message store for tenant '{tenantId}' refused a recent write and is in its retry cool-down, so this write was not attempted. See DurabilitySettings.TenantWriteBrakeCycle.")
    {
        TenantId = tenantId;
    }

    public string? TenantId { get; }
}
