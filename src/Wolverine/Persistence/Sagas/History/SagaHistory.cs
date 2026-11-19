using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     GH-5665. The message history of document sagas: which saga types record one, the call generated code makes to
///     record a row, and the reads behind <see cref="ISagaHistoryDiagnostics" />.
/// </summary>
public static class SagaHistory
{
    /// <summary>
    ///     Does <paramref name="sagaType" /> record a message history? True for a saga kept as a document or a row that
    ///     carries <see cref="RecordSagaHistoryAttribute" />, or for every such saga under
    ///     <c>opts.Policies.RecordSagaHistory()</c>. Never for an event-sourced saga, whose stream is its history.
    /// </summary>
    public static bool IsRecorded(Type sagaType, WolverineOptions? options)
    {
        if (!sagaType.CanBeCastTo<Saga>() || sagaType.CanBeCastTo<EventSourcedSaga>()) return false;

        return sagaType.HasAttribute<RecordSagaHistoryAttribute>() || options?.Durability.RecordSagaHistory == true;
    }

    /// <summary>
    ///     Called by generated code once a saga's handler has run, right before the saga is saved: records the message
    ///     in the saga's history, in the saga's own transaction where the saga's storage enlists in the outbox. A saga
    ///     that completes and is deleted has its history deleted with it instead. Not meant to be called from
    ///     application code.
    /// </summary>
    /// <param name="context">The message being handled</param>
    /// <param name="sagaType">The saga type</param>
    /// <param name="sagaId">The saga's identity</param>
    /// <param name="saga">The saga, or null when there is none to look at</param>
    /// <param name="handler">The saga method or methods that handled the message</param>
    /// <param name="step">Which part of the generated handler is recording</param>
    /// <param name="retained">Whether completed sagas of this type are retained (<see cref="RetainWhenCompletedAttribute" />)</param>
    /// <param name="token">Cancels the write</param>
    public static async Task RecordAsync(MessageContext context, Type sagaType, object? sagaId, Saga? saga,
        string handler, SagaHistoryStep step, bool retained, CancellationToken token)
    {
        if (sagaId == null) return;

        var completed = saga != null && saga.IsCompleted();
        var sagaTypeName = sagaType.FullName!;
        var id = sagaId.ToString()!;
        var tenantId = normalizeTenant(context.TenantId);

        // The history lives exactly as long as the saga: one that is deleted as it completes takes its history with it
        if (completed && !retained && step != SagaHistoryStep.NotFound)
        {
            await deleteAsync(context, sagaTypeName, id, tenantId, token).ConfigureAwait(false);
            return;
        }

        var outcome = step switch
        {
            SagaHistoryStep.Start => completed ? SagaHistoryOutcome.Completed : SagaHistoryOutcome.Started,
            SagaHistoryStep.Compensate => SagaHistoryOutcome.Compensated,
            SagaHistoryStep.NotFound => SagaHistoryOutcome.NotFound,
            _ => completed ? SagaHistoryOutcome.Completed : SagaHistoryOutcome.Continued
        };

        var record = new SagaHistoryRecord
        {
            SagaType = sagaTypeName,
            SagaId = id,
            TenantId = tenantId,
            MessageId = context.Envelope?.Id,
            MessageType = context.Envelope?.MessageType ?? context.Envelope?.Message?.GetType().ToMessageTypeName() ??
                          "unknown",
            Handler = handler,
            Outcome = outcome,
            CompletesSaga = completed && step != SagaHistoryStep.NotFound,
            SentMessages = sentMessagesOf(context)
        };

        await writeAsync(context, record, token).ConfigureAwait(false);
    }

    private static IReadOnlyList<SagaHistorySentMessage> sentMessagesOf(MessageContext context)
    {
        var list = new List<SagaHistorySentMessage>();
        foreach (var envelope in context.Outstanding)
        {
            var messageType = envelope.MessageType ?? envelope.Message?.GetType().ToMessageTypeName();
            if (messageType == null) continue;

            list.Add(new SagaHistorySentMessage(messageType, envelope.Id, envelope.Destination?.ToString(),
                envelope.ScheduledTime));
        }

        return list;
    }

    private static async Task writeAsync(MessageContext context, SagaHistoryRecord record, CancellationToken token)
    {
        if (context.Transaction is ISagaHistoryTransaction transaction &&
            await transaction.TryPersistSagaHistoryAsync(record, token).ConfigureAwait(false))
        {
            return;
        }

        // Best effort: the saga's storage does not enlist in the outbox, so the row goes in on its own
        var store = await storeForWritingAsync(context.Runtime, record.TenantId).ConfigureAwait(false);
        if (store == null)
        {
            warnOnce(context.Runtime, record.SagaType);
            return;
        }

        try
        {
            await store.RecordSagaHistoryAsync(record, token).ConfigureAwait(false);
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            context.Runtime.Logger.LogWarning(e,
                "Failed to record message {MessageType} {MessageId} in the history of saga {SagaType} {SagaId}",
                record.MessageType, record.MessageId, record.SagaType, record.SagaId);
        }
    }

    private static async Task deleteAsync(MessageContext context, string sagaType, string sagaId, string? tenantId,
        CancellationToken token)
    {
        if (context.Transaction is ISagaHistoryTransaction transaction &&
            await transaction.TryDeleteSagaHistoryAsync(sagaType, sagaId, tenantId, token).ConfigureAwait(false))
        {
            return;
        }

        var store = await storeForWritingAsync(context.Runtime, tenantId).ConfigureAwait(false);
        if (store == null) return;

        try
        {
            await store.DeleteSagaHistoryAsync(sagaType, sagaId, tenantId, token).ConfigureAwait(false);
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            context.Runtime.Logger.LogWarning(e, "Failed to delete the history of completed saga {SagaType} {SagaId}",
                sagaType, sagaId);
        }
    }

    private static readonly HashSet<string> _warned = new();

    private static void warnOnce(IWolverineRuntime runtime, string sagaType)
    {
        lock (_warned)
        {
            if (!_warned.Add(sagaType)) return;
        }

        runtime.Logger.LogWarning(
            "Saga {SagaType} records its message history, but the message store ({Store}) does not keep saga history, so none is recorded. The relational message stores, and Marten, Polecat and Fisher, keep it",
            sagaType, runtime.Storage.GetType().FullNameInCode());
    }

    private static string? normalizeTenant(string? tenantId)
    {
        if (tenantId.IsEmpty() || tenantId!.EqualsIgnoreCase(TransportConstants.Default) ||
            tenantId!.EqualsIgnoreCase(StorageConstants.DefaultTenantId))
        {
            return null;
        }

        return tenantId;
    }

    private static async ValueTask<ISagaHistoryStore?> storeForWritingAsync(IWolverineRuntime runtime,
        string? tenantId)
    {
        var main = runtime.Storage;
        if (main is MultiTenantedMessageStore tenanted)
        {
            return await tenanted.GetDatabaseAsync(tenantId).ConfigureAwait(false) as ISagaHistoryStore;
        }

        return main as ISagaHistoryStore;
    }

    /// <summary>
    ///     Whether this service keeps a message history for <paramref name="sagaType" />: the saga type records one and
    ///     the message store can keep it
    /// </summary>
    public static bool KeepsMessageHistory(IWolverineRuntime runtime, Type sagaType)
    {
        if (!IsRecorded(sagaType, runtime.Options)) return false;

        var main = runtime.Storage is MultiTenantedMessageStore tenanted ? tenanted.Main : runtime.Storage;
        return main is ISagaHistoryStore;
    }

    /// <summary>Marks a descriptor of a saga type that keeps a message history</summary>
    internal static void Describe(IWolverineRuntime runtime, SagaDescriptor descriptor, Type sagaType)
    {
        if (!KeepsMessageHistory(runtime, sagaType)) return;

        descriptor.SupportsHistory = true;
        descriptor.HistoryKind = SagaHistoryKind.MessageLog;
    }

    /// <summary>
    ///     Every message store a saga's history may be in: the tenant's, or every store when no tenant is named, since
    ///     a saga in an ancillary store records its history in that store
    /// </summary>
    internal static async ValueTask<IReadOnlyList<ISagaHistoryStore>> StoresForReadingAsync(IWolverineRuntime runtime,
        string? tenantId)
    {
        tenantId = normalizeTenant(tenantId);

        IEnumerable<IMessageStore> stores = tenantId == null
            ? await runtime.Stores.FindAllAsync().ConfigureAwait(false)
            : await runtime.Stores.FindForTenantAsync(tenantId).ConfigureAwait(false);

        var list = new List<ISagaHistoryStore>();
        foreach (var store in stores)
        {
            var candidate = store is MultiTenantedMessageStore tenanted
                ? await tenanted.GetDatabaseAsync(tenantId).ConfigureAwait(false)
                : store;

            if (candidate is ISagaHistoryStore history && !list.Contains(history)) list.Add(history);
        }

        return list;
    }

    /// <summary>One page of a document saga's message history, as a page of <see cref="ISagaHistoryDiagnostics" /></summary>
    internal static async Task<SagaHistoryPage> ReadPageAsync(IWolverineRuntime runtime, Type sagaType,
        object identity, SagaHistoryPageRequest page, CancellationToken token)
    {
        var pageNumber = Math.Max(1, page.Page);
        var pageSize = Math.Clamp(page.PageSize, 1, SagaHistoryPageRequest.MaxPageSize);
        var skip = (pageNumber - 1) * pageSize;
        var tenantId = normalizeTenant(page.TenantId);
        var sagaId = identity.ToString()!;

        var stores = await StoresForReadingAsync(runtime, tenantId).ConfigureAwait(false);

        IReadOnlyList<SagaHistoryRecord> records;
        long total;
        if (stores.Count == 1)
        {
            var rows = await stores[0].ReadSagaHistoryAsync(sagaType.FullName!, sagaId, tenantId, skip, pageSize, token)
                .ConfigureAwait(false);
            records = rows.Records;
            total = rows.TotalCount;
        }
        else
        {
            // The same saga id in several stores: merge the first skip + pageSize rows of each
            var all = new List<SagaHistoryRecord>();
            total = 0;
            foreach (var store in stores)
            {
                var rows = await store.ReadSagaHistoryAsync(sagaType.FullName!, sagaId, tenantId, 0, skip + pageSize,
                    token).ConfigureAwait(false);
                all.AddRange(rows.Records);
                total += rows.TotalCount;
            }

            records = all.OrderBy(x => x.RecordedAt).ThenBy(x => x.Id).Skip(skip).Take(pageSize).ToArray();
        }

        var events = records.Select((record, i) => new SagaHistoryEvent(
            skip + i + 1,
            record.RecordedAt,
            record.MessageType,
            runtime.Options.HandlerGraph.TryFindMessageType(record.MessageType, out var messageType)
                ? messageType.FullName
                : null,
            record.ToJson())).ToArray();

        return new SagaHistoryPage(sagaType.FullNameInCode(), identity, events, pageNumber, pageSize, total);
    }

    /// <summary>
    ///     Deletes the history of the sagas of <paramref name="sagaType" /> that completed before
    ///     <paramref name="completedBefore" />, in every message store. Run by the retention sweep with the same cutoff
    ///     it deletes the sagas by.
    /// </summary>
    internal static async Task<long> SweepAsync(IWolverineRuntime runtime, Type sagaType,
        DateTimeOffset completedBefore, CancellationToken token)
    {
        if (!IsRecorded(sagaType, runtime.Options)) return 0;

        long deleted = 0;
        foreach (var store in await StoresForReadingAsync(runtime, null).ConfigureAwait(false))
        {
            deleted += await store.DeleteHistoryOfSagasCompletedBeforeAsync(sagaType.FullName!, completedBefore, token)
                .ConfigureAwait(false);
        }

        return deleted;
    }
}
