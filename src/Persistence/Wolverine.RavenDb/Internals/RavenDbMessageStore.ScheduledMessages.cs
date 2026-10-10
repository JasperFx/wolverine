using JasperFx.Core;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime.Serialization;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IScheduledMessages
{
    public IScheduledMessages ScheduledMessages => this;

    async Task<ScheduledMessageResults> IScheduledMessages.QueryAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        var queryable = scheduledMessages(session, query);

        var totalCount = await queryable.CountAsync(token);

        if (query.PageNumber <= 0) query.PageNumber = 1;
        var skip = (query.PageNumber - 1) * query.PageSize;

        var messages = await queryable
            .OrderBy(x => x.ExecutionTime)
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(token);

        return new ScheduledMessageResults
        {
            PageNumber = query.PageNumber,
            TotalCount = totalCount,
            DatabaseUri = Uri,
            Messages = messages.Select(m => new ScheduledMessageSummary
            {
                Id = m.EnvelopeId,
                ReferenceKey = m.ReferenceKey,
                MessageType = m.MessageType,
                ScheduledTime = m.ExecutionTime,
                Destination = m.ReceivedAt?.ToString(),
                Attempts = m.Attempts,
                TenantId = EnvelopeSerializer.TryReadTenantId(m.Body)
            }).ToList()
        };
    }

    async Task IScheduledMessages.CancelAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var messages = await scheduledMessages(session, query).ToListAsync(token);
        foreach (var message in messages)
        {
            // A non-stale query only waits through its arrival cutoff. The index can still match
            // while the loaded document already has a newer state and change vector. Optimistic
            // concurrency protects changes after loading; recheck the loaded state as well.
            if (!matchesScheduledMessage(message, query)) continue;
            session.Delete(message);
        }

        await session.SaveChangesAsync(token);
    }

    private bool matchesScheduledMessage(IncomingMessage message, ScheduledMessageQuery query)
    {
        if (message.Status != EnvelopeStatus.Scheduled) return false;
        if (query.ReferenceKey != null &&
            (message.ReferenceKey != query.ReferenceKey || message.ServiceName != ServiceName)) return false;
        if (query.TenantId != null && message.TenantId != query.TenantId) return false;
        if (query.MessageIds.Length > 0 && !query.MessageIds.Contains(message.EnvelopeId)) return false;
        // The existing message-type index predicate is case-insensitive; reference and tenant are exact.
        if (query.MessageType.IsNotEmpty() &&
            !string.Equals(message.MessageType, query.MessageType, StringComparison.OrdinalIgnoreCase)) return false;
        if (query.ExecutionTimeFrom.HasValue && !(message.ExecutionTime >= query.ExecutionTimeFrom.Value)) return false;
        if (query.ExecutionTimeTo.HasValue && !(message.ExecutionTime <= query.ExecutionTimeTo.Value)) return false;
        return true;
    }

    private IRavenQueryable<IncomingMessage> scheduledMessages(IAsyncDocumentSession session, ScheduledMessageQuery query)
    {
        var queryable = session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.Status == EnvelopeStatus.Scheduled);

        if (query.ReferenceKey != null)
        {
            queryable = queryable.Where(x => x.ReferenceKey == query.ReferenceKey, exact: true)
                .Where(x => x.ServiceName == ServiceName, exact: true);
        }

        if (query.TenantId != null)
        {
            queryable = queryable.Where(x => x.TenantId == query.TenantId, exact: true);
        }

        if (query.MessageIds.Length > 0)
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.EnvelopeId.In(query.MessageIds));
        }

        if (query.MessageType.IsNotEmpty())
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.MessageType == query.MessageType);
        }

        if (query.ExecutionTimeFrom.HasValue && query.ExecutionTimeTo.HasValue)
        {
            // Keep the two bounds together so RavenDB can use a single range predicate.
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x =>
                x.ExecutionTime!.Value >= query.ExecutionTimeFrom.Value &&
                x.ExecutionTime!.Value <= query.ExecutionTimeTo.Value);
        }
        else if (query.ExecutionTimeFrom.HasValue)
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.ExecutionTime >= query.ExecutionTimeFrom.Value);
        }

        else if (query.ExecutionTimeTo.HasValue)
        {
            queryable = (IRavenQueryable<IncomingMessage>)queryable.Where(x => x.ExecutionTime <= query.ExecutionTimeTo.Value);
        }

        return queryable;
    }

    async Task IScheduledMessages.RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
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
