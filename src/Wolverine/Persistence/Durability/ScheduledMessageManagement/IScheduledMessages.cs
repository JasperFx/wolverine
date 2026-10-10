namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

public interface IScheduledMessages
{
    /// <summary>
    /// Query committed pending schedules. Results are observational and do not reserve work.
    /// Providers must reject unsupported filters rather than silently ignoring them.
    /// </summary>
    Task<ScheduledMessageResults> QueryAsync(ScheduledMessageQuery query, CancellationToken token);

    /// <summary>
    /// Cancel matching pending schedules. Pagination does not limit cancellation.
    /// Providers must reject unsupported filters. This cannot stop already-running work.
    /// RavenDB combines all supplied filters and throws a concurrency exception if selected
    /// documents change before its independent management transaction commits.
    /// </summary>
    Task CancelAsync(ScheduledMessageQuery query, CancellationToken token);
    Task RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token);
    Task<IReadOnlyList<ScheduledMessageCount>> SummarizeAsync(string serviceName, CancellationToken token);
}
