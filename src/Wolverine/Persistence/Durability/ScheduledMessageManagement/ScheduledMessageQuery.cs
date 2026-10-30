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
    /// Only scheduled messages whose envelope carries this saga id, such as a saga's pending
    /// timeouts. The saga id lives only in the stored envelope body, so the stores apply this
    /// filter in memory: every scheduled message matching the other criteria is read, then the
    /// matches are paged, which keeps <see cref="PageSize"/> and the total count exact. Narrow it
    /// with <see cref="MessageType"/> or an execution time window on a large scheduled backlog.
    /// </summary>
    public string? SagaId { get; set; }

    /// <summary>
    /// Purely a marker for request/response scenarios
    /// </summary>
    public Guid QueryId { get; set; } = Guid.NewGuid();
}
