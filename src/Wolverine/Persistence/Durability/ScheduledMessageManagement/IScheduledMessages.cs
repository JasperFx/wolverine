namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

public interface IScheduledMessages
{
    Task<ScheduledMessageResults> QueryAsync(ScheduledMessageQuery query, CancellationToken token);
    Task CancelAsync(ScheduledMessageQuery query, CancellationToken token);
    Task RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token);

    /// <summary>
    /// GH-5167. Move every scheduled message matching the query, such as every message scheduled with one
    /// <see cref="ScheduledMessageQuery.GroupId"/>, to a new execution time. The query must name at least one
    /// filter. Only messages still waiting move: a message already handed off for execution may still run.
    /// </summary>
    Task RescheduleAsync(ScheduledMessageQuery query, DateTimeOffset newExecutionTime, CancellationToken token)
    {
        return ScheduledMessageSagaFilter.RescheduleEachAsync(this, query, newExecutionTime, token);
    }
    Task<IReadOnlyList<ScheduledMessageCount>> SummarizeAsync(string serviceName, CancellationToken token);
}
