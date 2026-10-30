using JasperFx.Core;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IScheduledMessages
{
    public IScheduledMessages ScheduledMessages => this;

    async Task<ScheduledMessageResults> IScheduledMessages.QueryAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        if (query.SagaId.IsNotEmpty())
        {
            return await ScheduledMessageSagaFilter.QueryAsync(this, query, token);
        }

        using var session = _store.OpenAsyncSession();
        var queryable = session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.Status == EnvelopeStatus.Scheduled);

        if (query.MessageIds.Length > 0)
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.EnvelopeId.In(query.MessageIds));
        }

        if (query.MessageType.IsNotEmpty())
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.MessageType == query.MessageType);
        }

        if (query.ExecutionTimeFrom.HasValue)
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.ExecutionTime >= query.ExecutionTimeFrom.Value);
        }

        if (query.ExecutionTimeTo.HasValue)
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.ExecutionTime <= query.ExecutionTimeTo.Value);
        }

        var totalCount = await queryable.CountAsync(token);

        if (query.PageNumber <= 0) query.PageNumber = 1;
        var ordered = queryable.OrderBy(x => x.ExecutionTime);

        // A page size of zero or less reads every match, which the saga id filter relies on
        var messages = query.PageSize > 0
            ? await ordered.Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize).ToListAsync(token)
            : await ordered.ToListAsync(token);

        return new ScheduledMessageResults
        {
            PageNumber = query.PageNumber,
            TotalCount = totalCount,
            DatabaseUri = Uri,
            Messages = messages.Select(m =>
            {
                var summary = new ScheduledMessageSummary
                {
                    Id = m.EnvelopeId,
                    MessageType = m.MessageType,
                    ScheduledTime = m.ExecutionTime,
                    Destination = m.ReceivedAt?.ToString(),
                    Attempts = m.Attempts
                };

                summary.ReadEnvelopeIdentity(m.Body);
                return summary;
            }).ToList()
        };
    }

    async Task IScheduledMessages.CancelAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        if (query.SagaId.IsNotEmpty())
        {
            var bySagaIds = await ScheduledMessageSagaFilter.ResolveCancellationAsync(this, query, token);
            if (bySagaIds == null) return;
            query = bySagaIds;
        }

        using var session = _store.OpenAsyncSession();

        if (query.MessageIds.Length > 0)
        {
            foreach (var id in query.MessageIds)
            {
                // Find the incoming message by envelope id
                var messages = await session.Query<IncomingMessage>()
                    .Customize(x => x.WaitForNonStaleResults())
                    .Where(x => x.EnvelopeId == id && x.Status == EnvelopeStatus.Scheduled)
                    .ToListAsync(token);

                foreach (var msg in messages)
                {
                    session.Delete(msg.Id);
                }
            }

            await session.SaveChangesAsync(token);
            return;
        }

        // Query-based cancel
        var queryable = session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.Status == EnvelopeStatus.Scheduled);

        if (query.MessageType.IsNotEmpty())
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.MessageType == query.MessageType);
        }

        if (query.ExecutionTimeFrom.HasValue)
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.ExecutionTime >= query.ExecutionTimeFrom.Value);
        }

        if (query.ExecutionTimeTo.HasValue)
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.ExecutionTime <= query.ExecutionTimeTo.Value);
        }

        var toDelete = await queryable.ToListAsync(token);
        foreach (var msg in toDelete)
        {
            session.Delete(msg.Id);
        }

        await session.SaveChangesAsync(token);
    }

    async Task IScheduledMessages.RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        var messages = await session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.EnvelopeId == envelopeId && x.Status == EnvelopeStatus.Scheduled)
            .ToListAsync(token);

        foreach (var msg in messages)
        {
            msg.ExecutionTime = newExecutionTime.ToUniversalTime();
        }

        await session.SaveChangesAsync(token);
    }

    async Task<IReadOnlyList<ScheduledMessageCount>> IScheduledMessages.SummarizeAsync(string serviceName, CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        var messages = await session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.Status == EnvelopeStatus.Scheduled)
            .ToListAsync(token);

        return messages
            .GroupBy(x => x.MessageType)
            .Select(g => new ScheduledMessageCount(serviceName, g.Key, Uri, g.Count()))
            .ToList();
    }
}
