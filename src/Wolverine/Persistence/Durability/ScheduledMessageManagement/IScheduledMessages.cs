namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

public interface IScheduledMessages
{
    Task<ScheduledMessageResults> QueryAsync(ScheduledMessageQuery query, CancellationToken token);
    Task CancelAsync(ScheduledMessageQuery query, CancellationToken token);
    Task RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token);
    Task<IReadOnlyList<ScheduledMessageCount>> SummarizeAsync(string serviceName, CancellationToken token);
}
