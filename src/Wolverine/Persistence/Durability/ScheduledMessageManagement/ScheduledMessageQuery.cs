namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

public class ScheduledMessageQuery
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Exact, case-sensitive business reference. Null does not filter by reference.
    /// Reference queries are scoped to the current application's service name.
    /// Supported by RavenDB and PostgreSQL (including Marten).
    /// </summary>
    public string? ReferenceKey { get; set; }

    /// <summary>
    /// Exact, case-sensitive tenant filter. Null includes all tenants in the store.
    /// Supported by RavenDB and PostgreSQL (including Marten). This filter is not an authorization boundary.
    /// </summary>
    public string? TenantId { get; set; }

    public string? MessageType { get; set; }
    public DateTimeOffset? ExecutionTimeFrom { get; set; }
    public DateTimeOffset? ExecutionTimeTo { get; set; }
    public Guid[] MessageIds { get; set; } = [];

    /// <summary>
    /// Purely a marker for request/response scenarios
    /// </summary>
    public Guid QueryId { get; set; } = Guid.NewGuid();

    internal void ThrowIfReferenceFilteringIsUnsupported()
    {
        if (ReferenceKey != null || TenantId != null)
        {
            throw new NotSupportedException("This message store does not support scheduled message reference or tenant filtering.");
        }
    }
}
