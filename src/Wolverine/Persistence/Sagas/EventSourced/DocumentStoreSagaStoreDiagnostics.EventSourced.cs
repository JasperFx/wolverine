using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.Events.Projections;

namespace Wolverine.Persistence.Sagas;

// GH-5415: the event-sourced half of a document store's saga diagnostics -- the history reads, the Live read, and the
// retention sweep that archives a completed saga's stream
internal sealed partial class DocumentStoreSagaStoreDiagnostics : ISagaHistoryDiagnostics
{
    private bool isLiveEventSourced(Type sagaType)
        => sagaType.CanBeCastTo<EventSourcedSaga>() &&
           _runtime.Options.EventSourcedSagas.LifecycleOf(sagaType) == ProjectionLifecycle.Live;

    public async Task<SagaHistoryPage> ReadSagaHistoryAsync(string sagaTypeName, object identity,
        SagaHistoryPageRequest page, CancellationToken ct)
    {
        var sagaType = eventSourcedSagaType(sagaTypeName);
        var pageNumber = Math.Max(1, page.Page);
        var pageSize = Math.Clamp(page.PageSize, 1, SagaHistoryPageRequest.MaxPageSize);

        await using var session = _sessions.QuerySession();
        var events = EventSessionAccess.QueryEventsOf(session);
        var streamId = EventSessionAccess.StreamIdentityOf(identity);

        var all = streamId is Guid guid
            ? await events.FetchStreamAsync(guid, token: ct).ConfigureAwait(false)
            : await events.FetchStreamAsync(streamId.ToString()!, token: ct).ConfigureAwait(false);

        var slice = all.OrderBy(x => x.Version).Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(toHistoryEvent).ToArray();

        return new SagaHistoryPage(sagaType.FullNameInCode(), identity, slice, pageNumber, pageSize, all.Count);
    }

    public async Task<SagaInstanceState?> ReadSagaAtAsync(string sagaTypeName, object identity, long version,
        CancellationToken ct)
    {
        var sagaType = eventSourcedSagaType(sagaTypeName);
        var saga = await aggregateAsync(sagaType, identity, version, null, ct).ConfigureAwait(false);
        return saga == null ? null : buildInstance(sagaType, identity, saga);
    }

    public async Task<SagaInstanceState?> ReadSagaAtAsync(string sagaTypeName, object identity, DateTimeOffset timestamp,
        CancellationToken ct)
    {
        var sagaType = eventSourcedSagaType(sagaTypeName);
        var saga = await aggregateAsync(sagaType, identity, 0, timestamp, ct).ConfigureAwait(false);
        return saga == null ? null : buildInstance(sagaType, identity, saga);
    }

    private Type eventSourcedSagaType(string sagaTypeName)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var entry))
        {
            throw new NotSupportedException($"{_storeName} keeps no saga of type {sagaTypeName}.");
        }

        if (!entry.SagaType.CanBeCastTo<EventSourcedSaga>())
        {
            throw new NotSupportedException(
                $"{entry.SagaType.FullNameInCode()} is kept as a document, so {_storeName} keeps no history of it. Only event-sourced sagas have one.");
        }

        return entry.SagaType;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code.")]
    private async Task<object?> aggregateAsync(Type sagaType, object identity, long version, DateTimeOffset? timestamp,
        CancellationToken ct)
    {
        await using var session = _sessions.QuerySession();
        var events = EventSessionAccess.QueryEventsOf(session);

        var task = (Task<object?>)typeof(DocumentStoreSagaStoreDiagnostics)
            .GetMethod(nameof(aggregateStreamAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType)
            .Invoke(null, [events, EventSessionAccess.StreamIdentityOf(identity), version, timestamp, ct])!;
        return await task.ConfigureAwait(false);
    }

    private static async Task<object?> aggregateStreamAsync<TSaga>(IQueryEventStore events, object streamId, long version,
        DateTimeOffset? timestamp, CancellationToken ct) where TSaga : class
    {
        return streamId is Guid guid
            ? await events.AggregateStreamAsync<TSaga>(guid, version, timestamp, token: ct).ConfigureAwait(false)
            : await events.AggregateStreamAsync<TSaga>(streamId.ToString()!, version, timestamp, token: ct).ConfigureAwait(false);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Diagnostics only: serializes event bodies for a monitoring tool.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Diagnostics only: serializes event bodies for a monitoring tool.")]
    private static SagaHistoryEvent toHistoryEvent(IEvent e)
        => new(e.Version, e.Timestamp, e.EventTypeName, e.EventType.FullName ?? e.DotNetTypeName,
            JsonSerializer.SerializeToElement(e.Data, e.EventType));

    // GH-5839: an operator completes an event-sourced saga with the same SagaCompleted event MarkCompleted() appends,
    // carrying the reason, and with the Archived event too for an [ArchiveWhenCompleted] saga. FetchForWriting makes the
    // append optimistic: a saga that gained an event since it was read is refused with the store's concurrency error
    private static async Task<SagaTerminationOutcome> completeEventSourcedAsync<TSaga>(IDocumentSessionFactory sessions,
        object streamId, string reason, CancellationToken ct) where TSaga : EventSourcedSaga
    {
        await using var session = sessions.LightweightSession();
        var events = EventSessionAccess.EventOperationsOf(session);

        var stream = streamId is Guid guid
            ? await events.FetchForWriting<TSaga>(guid, ct).ConfigureAwait(false)
            : await events.FetchForWriting<TSaga>(streamId.ToString()!, ct).ConfigureAwait(false);

        if (stream.Aggregate is null) return SagaTerminationOutcome.NotFound;
        if (stream.Aggregate.IsCompleted()) return SagaTerminationOutcome.AlreadyCompleted;

        stream.AppendOne(new SagaCompleted { Reason = reason });
        if (typeof(TSaga).HasAttribute<ArchiveWhenCompletedAttribute>())
        {
            stream.AppendOne(new Archived(ArchiveWhenCompletedAttribute.Reason));
        }

        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return SagaTerminationOutcome.CompletionAppended;
    }

    // GH-5415 / GH-5572: a completed event-sourced saga past its retention period has its stream archived with
    // JasperFx.Events' Archived event, and its snapshot removed. Events are never deleted
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code.")]
    private async Task<long> archiveCompletedAsync(Type sagaType, DateTimeOffset completedBefore, CancellationToken ct)
    {
        var task = (Task<long>)typeof(DocumentStoreSagaStoreDiagnostics)
            .GetMethod(nameof(archiveCompletedOfAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType)
            .Invoke(null, [_sessions, completedBefore, ct])!;
        return await task.ConfigureAwait(false);
    }

    private static async Task<long> archiveCompletedOfAsync<TSaga>(IDocumentSessionFactory sessions,
        DateTimeOffset completedBefore, CancellationToken ct) where TSaga : Saga
    {
        List<TSaga> completed;
        await using (var query = sessions.QuerySession())
        {
            completed = (await SagaCompletionQueries.CompletedBefore(query.Query<TSaga>(), completedBefore)
                .ToListAsync(ct).ConfigureAwait(false)).ToList();
        }

        if (completed.Count == 0) return 0;

        // The Archived event first, in a session of its own: the saga's Inline snapshot applies it, so the snapshot
        // is only deleted afterwards
        await using (var session = sessions.LightweightSession())
        {
            var events = EventSessionAccess.EventOperationsOf(session);
            foreach (var saga in completed)
            {
                var streamId = EventSessionAccess.StreamIdentityOf(extractIdentity(saga, typeof(TSaga))!);
                if (streamId is Guid guid)
                {
                    events.Append(guid, EventSourcedSagaRegistration.ArchivedForRetention());
                }
                else
                {
                    events.Append(streamId.ToString()!, EventSourcedSagaRegistration.ArchivedForRetention());
                }
            }

            await session.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await using (var session = sessions.LightweightSession())
        {
            session.DeleteWhere<TSaga>(x => x.CompletedAt != null && x.CompletedAt < completedBefore);
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return completed.Count;
    }
}
