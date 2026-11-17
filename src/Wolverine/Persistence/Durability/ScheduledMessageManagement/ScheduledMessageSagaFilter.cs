using Wolverine.Runtime.Serialization;

namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

/// <summary>
/// The <see cref="ScheduledMessageQuery.SagaId"/> and <see cref="ScheduledMessageQuery.TenantId"/> filters,
/// shared by the message stores. No store has a saga id or tenant id column on its scheduled messages, so the
/// filter reads every scheduled message matching the rest of the query and applies those ids and the paging
/// in memory.
/// </summary>
public static class ScheduledMessageSagaFilter
{
    /// <summary>
    /// Does this query name a saga or tenant id, which the stores can only apply in memory?
    /// </summary>
    public static bool AppliesTo(ScheduledMessageQuery query)
    {
        return !string.IsNullOrEmpty(query.SagaId) || !string.IsNullOrEmpty(query.TenantId);
    }

    /// <summary>
    /// Fill in the tenant and saga ids a summary carries from its stored envelope body
    /// </summary>
    public static void ReadEnvelopeIdentity(this ScheduledMessageSummary summary, byte[]? body)
    {
        (summary.TenantId, summary.SagaId) = EnvelopeSerializer.TryReadTenantAndSagaId(body);
    }

    /// <summary>
    /// Answer a query that names a saga id: read the unpaged matches of everything else in the query,
    /// keep the saga's messages, then page those
    /// </summary>
    public static async Task<ScheduledMessageResults> QueryAsync(IScheduledMessages store,
        ScheduledMessageQuery query, CancellationToken token)
    {
        if (query.PageNumber <= 0) query.PageNumber = 1;

        var unpaged = await store.QueryAsync(withoutSagaOrPaging(query), token);
        var matching = unpaged.Messages
            .Where(x => string.IsNullOrEmpty(query.SagaId) || x.SagaId == query.SagaId)
            .Where(x => string.IsNullOrEmpty(query.TenantId) || x.TenantId == query.TenantId)
            .ToList();

        var messages = query.PageSize > 0
            ? matching.Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize).ToList()
            : matching;

        return new ScheduledMessageResults
        {
            PageNumber = query.PageNumber,
            TotalCount = matching.Count,
            DatabaseUri = unpaged.DatabaseUri,
            Messages = messages
        };
    }

    /// <summary>
    /// Turn a cancellation that names a saga id into one by the ids of the saga's matching messages,
    /// or null when the saga has nothing scheduled that matches. An empty id list would mean "no id
    /// filter" to the stores, so a null must not fall through to a cancel.
    /// </summary>
    public static async Task<ScheduledMessageQuery?> ResolveCancellationAsync(IScheduledMessages store,
        ScheduledMessageQuery query, CancellationToken token)
    {
        var matched = await QueryAsync(store, new ScheduledMessageQuery
        {
            MessageType = query.MessageType,
            ExecutionTimeFrom = query.ExecutionTimeFrom,
            ExecutionTimeTo = query.ExecutionTimeTo,
            MessageIds = query.MessageIds,
            SagaId = query.SagaId,
            GroupId = query.GroupId,
            TenantId = query.TenantId,
            PageSize = 0
        }, token);

        if (matched.Messages.Count == 0) return null;

        return new ScheduledMessageQuery { MessageIds = matched.Messages.Select(x => x.Id).ToArray() };
    }

    /// <summary>
    /// GH-5167. Move every scheduled message matching the query to a new execution time, one envelope id at a
    /// time. The fallback for stores that cannot do it in one statement. Only messages still scheduled move: one
    /// already handed off for execution may still run.
    /// </summary>
    public static async Task RescheduleEachAsync(IScheduledMessages store, ScheduledMessageQuery query,
        DateTimeOffset newExecutionTime, CancellationToken token)
    {
        AssertHasCriteria(query);

        var matched = await store.QueryAsync(new ScheduledMessageQuery
        {
            MessageType = query.MessageType,
            ExecutionTimeFrom = query.ExecutionTimeFrom,
            ExecutionTimeTo = query.ExecutionTimeTo,
            MessageIds = query.MessageIds,
            SagaId = query.SagaId,
            GroupId = query.GroupId,
            TenantId = query.TenantId,
            PageSize = 0
        }, token);

        foreach (var message in matched.Messages)
        {
            await store.RescheduleAsync(message.Id, newExecutionTime, token);
        }
    }

    /// <summary>
    /// A reschedule by query has to name what it moves, so that an empty query cannot move every scheduled
    /// message in the database to the same time
    /// </summary>
    public static void AssertHasCriteria(ScheduledMessageQuery query)
    {
        if (!query.HasCriteria)
        {
            throw new ArgumentException(
                "Rescheduling by query requires at least one filter, such as GroupId, MessageIds, MessageType, SagaId, TenantId or an execution time window",
                nameof(query));
        }
    }

    private static ScheduledMessageQuery withoutSagaOrPaging(ScheduledMessageQuery query)
    {
        return new ScheduledMessageQuery
        {
            MessageType = query.MessageType,
            ExecutionTimeFrom = query.ExecutionTimeFrom,
            ExecutionTimeTo = query.ExecutionTimeTo,
            MessageIds = query.MessageIds,
            GroupId = query.GroupId,
            QueryId = query.QueryId,
            PageNumber = 1,
            PageSize = 0
        };
    }
}
