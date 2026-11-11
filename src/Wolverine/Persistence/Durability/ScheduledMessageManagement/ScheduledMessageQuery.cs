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
    /// GH-5167. Only scheduled messages whose envelope carries this <see cref="Envelope.GroupId"/>, such as
    /// <c>booking/{bookingId}</c> for every reminder scheduled on behalf of one booking. Set it on the way out
    /// with <see cref="DeliveryOptions.GroupId"/>. The relational stores keep the group id in its own indexed
    /// column, so this filter runs in the database.
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// GH-5167. When set, scopes the query to one tenant. For a multi-tenanted message store this routes the
    /// query, cancellation or reschedule to the tenant's database. A message store that serves a single
    /// database keeps only the scheduled messages whose stored envelope carries this tenant id, which it applies
    /// in memory like <see cref="SagaId"/>
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Purely a marker for request/response scenarios
    /// </summary>
    public Guid QueryId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// True when the query names anything at all to narrow the scheduled messages by
    /// </summary>
    public bool HasCriteria => MessageType != null || ExecutionTimeFrom.HasValue || ExecutionTimeTo.HasValue ||
                               MessageIds.Length > 0 || SagaId != null || GroupId != null || TenantId != null;
}
