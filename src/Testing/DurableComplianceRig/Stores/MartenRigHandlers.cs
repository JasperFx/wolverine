using System.Collections.Concurrent;
using DurableComplianceRig.Infrastructure;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Marten;
using Marten.Services;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace DurableComplianceRig.Stores;

/// <summary>
/// Writes a rig handler's ledger rows into its Marten session's unit of work, so they commit (or not) with
/// everything else Wolverine's Marten middleware saves -- including, on a durable endpoint, the inbox row's mark
/// as handled.
/// </summary>
public class MartenLedger(RigDatabase database)
{
    public async Task RecordAsync(IDocumentSession session, IMessageContext context, Envelope envelope,
        IReadOnlyList<Guid> ids, RigNodeName node)
    {
        var begun = new List<(Guid id, Step step, Execution execution)>();
        foreach (var id in ids)
        {
            var (step, execution) = await Script.BeginAsync(id, node.Name, envelope.Message is RigItem[] ? null : envelope.Id);
            begun.Add((id, step, execution));
        }

        // F13: reschedule from inside the handler and then return normally, writing nothing
        if (ids.Count == 1 && MartenRigHooks.TryTakeReschedule(ids[0]))
        {
            await ((IEnvelopeLifecycle)context).ReScheduleAsync(DateTimeOffset.UtcNow.AddSeconds(1));

            // Diagnostic switches for F13, to tell which completion overwrites the Scheduled row:
            //   skip-tx    the Marten commit listener skips its in-transaction mark (it needs WasPersistedInInbox)
            //   skip-receiver  the durable receiver's completion skips (it skips an envelope already Handled)
            //   skip-both      both skip
            var experiment = Environment.GetEnvironmentVariable("RIG_F13_EXPERIMENT");
            if (experiment is "skip-tx" or "skip-both") envelope.WasPersistedInInbox = false;
            if (experiment is "skip-receiver" or "skip-both") envelope.Status = EnvelopeStatus.Handled;

            begun[0].execution.Finish("rescheduled itself and returned");
            return;
        }

        foreach (var (id, _, execution) in begun)
        {
            // Positional '?' placeholders, and no ';': QueueSqlCommand rejects a statement containing one
            session.QueueSqlCommand(database.InsertLedgerSql
                    .Replace("@message_id", "?").Replace("@attempt", "?").Replace("@node", "?")
                    .Replace("@tenant", "?").Replace("@started_at", "?").Replace("@recorded_at", "?"),
                id, execution.Attempt, execution.Node, (object?)envelope.TenantId ?? DBNull.Value,
                execution.StartedAt, DateTimeOffset.UtcNow);
        }

        session.Listeners.Add(new FinishOnCommit(begun.Select(x => x.execution).ToArray()));

        // Diagnostic switch: proves the crash-after-commit spec can fail. Without WasPersistedInInbox the commit
        // listener skips its in-transaction mark, leaving the row to the receiver's completion after the commit
        if (Environment.GetEnvironmentVariable("RIG_SKIP_TX_MARK") == "1" && ids.Any(MartenRigHooks.HasHold))
        {
            envelope.WasPersistedInInbox = false;
        }

        foreach (var (_, step, execution) in begun) Script.BeforeCommit(step, execution);
    }

    /// <summary>
    /// The handler returns before the middleware commits, so an execution is only finished once the commit is
    /// known. Finished provisionally just before the save too: a failed commit never reaches AfterCommitAsync, and
    /// an execution left open would look like it overlapped every later attempt.
    /// </summary>
    private class FinishOnCommit(Execution[] executions) : DocumentSessionListenerBase
    {
        public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            foreach (var execution in executions) execution.Finish("saving");
            return Task.CompletedTask;
        }

        public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        {
            foreach (var execution in executions) execution.Finish("committed");

            // After the commit and before the receiver settles the row: the window a crash has to hit to tell a
            // row marked inside the transaction apart from one marked after it
            foreach (var execution in executions)
            {
                await MartenRigHooks.HoldAfterCommitAsync(execution.MessageId);
            }
        }
    }
}

/// <summary>The rig handlers for Marten. Transactional through <c>AutoApplyTransactions</c>.</summary>
public static class MartenRigHandler
{
    public static Task Handle(RigMessage message, Envelope envelope, IDocumentSession session, IMessageContext context,
        RigNodeName node, MartenLedger ledger) => ledger.RecordAsync(session, context, envelope, [message.Id], node);

    public static Task Handle(RigItem[] items, Envelope envelope, IDocumentSession session, IMessageContext context,
        RigNodeName node, MartenLedger ledger) =>
        ledger.RecordAsync(session, context, envelope, items.Select(x => x.Id).ToArray(), node);
}

/// <summary>
/// Marten-specific handler behaviour that <see cref="Script" /> does not model. Static for the same reason
/// Script is: handlers are code generated and scenarios run one at a time.
/// </summary>
public static class MartenRigHooks
{
    private static readonly ConcurrentDictionary<Guid, int> _reschedules = new();
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _holds = new();
    private static readonly ConcurrentDictionary<Guid, int> _deliveries = new();

    public static void Reset()
    {
        _reschedules.Clear();
        foreach (var hold in _holds.Values) hold.TrySetResult();
        _holds.Clear();
        _heldNow.Clear();
        _deliveries.Clear();
    }

    /// <summary>The handler calls <c>ReScheduleAsync</c> and returns normally on its first <paramref name="times" /> runs.</summary>
    public static void RescheduleFirst(Guid id, int times) => _reschedules[id] = times;

    public static bool TryTakeReschedule(Guid id)
    {
        while (_reschedules.TryGetValue(id, out var left) && left > 0)
        {
            if (_reschedules.TryUpdate(id, left - 1, left)) return true;
        }

        return false;
    }

    /// <summary>The message's first commit holds in AfterCommitAsync until <see cref="ReleaseHold" />.</summary>
    public static void HoldAfterFirstCommit(Guid id) =>
        _holds[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public static bool HasHold(Guid id) => _holds.ContainsKey(id);

    public static void ReleaseHold(Guid id)
    {
        if (_holds.TryRemove(id, out var hold)) hold.TrySetResult();
    }

    /// <summary>The message's handler has committed and is now waiting in AfterCommitAsync.</summary>
    public static bool IsHeld(Guid id) => _heldNow.ContainsKey(id);

    private static readonly ConcurrentDictionary<Guid, bool> _heldNow = new();

    public static async Task HoldAfterCommitAsync(Guid id)
    {
        if (!_holds.TryGetValue(id, out var hold)) return;

        _heldNow[id] = true;
        try
        {
            await hold.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally
        {
            _heldNow.TryRemove(id, out _);

            // Only the first commit is held
            _holds.TryRemove(id, out _);
        }
    }

    public static void CountDelivery(Envelope envelope) => _deliveries.AddOrUpdate(envelope.Id, 1, (_, n) => n + 1);

    /// <summary>
    /// Deliveries that reached the handler chain, counted before any idempotency check runs. Without this an
    /// idempotency spec could pass because the duplicate never arrived at all.
    /// </summary>
    public static int DeliveriesOf(Guid id) => _deliveries.TryGetValue(id, out var count) ? count : 0;
}

/// <summary>
/// Puts <see cref="MartenRigHooks.CountDelivery" /> first in every rig handler chain, ahead of the Marten session
/// frame that runs the eager idempotency check (it is appended to the middleware, so index 0 precedes it).
/// </summary>
public class CountDeliveriesPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(x => x.MessageType == typeof(RigMessage)))
        {
            chain.Middleware.Insert(0, new MethodCall(typeof(MartenRigHooks), nameof(MartenRigHooks.CountDelivery)));
        }
    }
}
