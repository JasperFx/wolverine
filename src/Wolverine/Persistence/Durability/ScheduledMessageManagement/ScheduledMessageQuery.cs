namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

public class ScheduledMessageQuery
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 100;
    public string? MessageType { get; set; }
    public DateTimeOffset? ExecutionTimeFrom { get; set; }
    public DateTimeOffset? ExecutionTimeTo { get; set; }
    public Guid[] MessageIds { get; set; } = [];

    /// <summary>
    /// Purely a marker for request/response scenarios
    /// </summary>
    public Guid QueryId { get; set; } = Guid.NewGuid();
}
