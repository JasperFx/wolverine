using Wolverine.Runtime.Serialization;

namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

/// <summary>
/// The <see cref="ScheduledMessageQuery.SagaId"/> filter, shared by the message stores. No store has a
/// saga id column, so the filter reads every scheduled message matching the rest of the query and
/// applies the saga id and the paging in memory.
/// </summary>
public static class ScheduledMessageSagaFilter
{
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
        var matching = unpaged.Messages.Where(x => x.SagaId == query.SagaId).ToList();

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
            PageSize = 0
        }, token);

        if (matched.Messages.Count == 0) return null;

        return new ScheduledMessageQuery { MessageIds = matched.Messages.Select(x => x.Id).ToArray() };
    }

    private static ScheduledMessageQuery withoutSagaOrPaging(ScheduledMessageQuery query)
    {
        return new ScheduledMessageQuery
        {
            MessageType = query.MessageType,
            ExecutionTimeFrom = query.ExecutionTimeFrom,
            ExecutionTimeTo = query.ExecutionTimeTo,
            MessageIds = query.MessageIds,
            QueryId = query.QueryId,
            PageNumber = 1,
            PageSize = 0
        };
    }
}
