using Bobcat;
using Bobcat.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace DurableComplianceRig.Infrastructure;

/// <summary>
/// Steps for <c>BatchMessagesOf&lt;RigItem&gt;()</c> on durable endpoints. Batch members are keyed by their own ids,
/// exactly like single messages, so the same invariants judge them; these steps add the error policies a batch
/// carrier is retried under, the outcomes a whole failing batch drags every member into, and the checks that only
/// make sense for a batch.
/// </summary>
public sealed partial class DurableSteps
{
    private bool _deadLetterStorageRefusing;

    public BatchedTransport Batched =>
        Transport as BatchedTransport
        ?? throw new SpecCriticalException($"{Transport} does not batch its items; use a {nameof(BatchedTransport)}");

    /// <summary>Not a step: every batching scenario captures its nodes' errors. Renders nothing.</summary>
    public void CaptureErrorLogs()
    {
        RigLogCapture.Reset();
        ConfigureNodes(opts => opts.Services.AddSingleton<ILoggerProvider, RigLogCapture>());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Error policies for the batch carrier. Policies are global, so they apply to the RigItem[] chain.

    [Given("a failed batch is retried on a schedule {times} time(s), {milliseconds} ms apart")]
    public void FailedBatchesAreScheduledForRetry(int times, int milliseconds) =>
        ConfigureNodes(opts => opts.OnException<RigFailure>()
            .ScheduleRetry(Enumerable.Repeat(milliseconds.Milliseconds(), times).ToArray()));

    [Given("a failed batch is retried with a cooldown {times} time(s), {milliseconds} ms apart")]
    public void FailedBatchesAreRetriedWithCooldown(int times, int milliseconds) =>
        ConfigureNodes(opts => opts.OnException<RigFailure>()
            .RetryWithCooldown(Enumerable.Repeat(milliseconds.Milliseconds(), times).ToArray()));

    [Given("a failed batch is requeued, up to {attempts} attempts")]
    public void FailedBatchesAreRequeued(int attempts) =>
        ConfigureNodes(opts => opts.OnException<RigFailure>().Requeue(attempts));

    [Given("a failed batch is moved to the error queue")]
    public void FailedBatchesAreDeadLettered() =>
        ConfigureNodes(opts => opts.OnException<RigFailure>().MoveToErrorQueue());

    [Given("a failed batch is discarded")]
    public void FailedBatchesAreDiscarded() =>
        ConfigureNodes(opts => opts.OnException<RigFailure>().Discard());

    [Given("a failed batch is probed one member at a time after {attempts} whole-batch failure(s)")]
    public void FailedBatchesAreProbedIndividually(int attempts) =>
        Batched.ConfigureBatch(batch => batch.ProbeIndividuallyAfter(attempts));

    [Given("a failed batch has its members isolated")]
    public void FailedBatchesIsolateTheirMembers() =>
        ConfigureNodes(opts => opts.OnException<RigFailure>().IsolateBatchMembers());

    // ---------------------------------------------------------------------------------------------------------
    // What the failing batch does to its members

    [Given("every item rides the failing batch to the dead letter queue")]
    public void EveryItemIsDeadLetteredWithTheBatch()
    {
        foreach (var id in _messages) _expected[id] = Outcome.DeadLettered;
    }

    [Given("every item is discarded with the failing batch")]
    public void EveryItemIsDiscardedWithTheBatch()
    {
        foreach (var id in _messages) _expected[id] = Outcome.Discarded;
    }

    [Given("item {number} is poison: the batch handler names it in an ApplyItemException and replays the rest")]
    public void ItemIsPoisonForApplyItem(int number)
    {
        var id = MessageNumbered(number);
        BatchItemScript.PoisonForApplyItem(id);
        _expected[id] = Outcome.DeadLettered;
    }

    [Given("the inbox stale time is {milliseconds} ms")]
    public void InboxStaleTimeIs(int milliseconds) =>
        ConfigureNodes(opts => opts.Durability.InboxStaleTime = milliseconds.Milliseconds());

    // ---------------------------------------------------------------------------------------------------------
    // Dead letter storage that fails. A CHECK constraint that no row can pass, added NOT VALID so the rows
    // already there are left alone: every insert into the dead letter table fails, nothing else is touched.

    private const string RefusalConstraint = "rig_refuse_dead_letters";

    [Given("dead letter storage refuses every write")]
    public async Task DeadLetterStorageRefusesWrites()
    {
        foreach (var database in Store.Databases)
        {
            await executeAsync(database, database.Dialect == Dialect.Postgresql
                ? $"alter table {database.Schema}.wolverine_dead_letters add constraint {RefusalConstraint} check (false) not valid"
                : $"alter table {database.Schema}.wolverine_dead_letters with nocheck add constraint {RefusalConstraint} check (1 = 0)");
        }

        _deadLetterStorageRefusing = true;
    }

    [When("dead letter storage accepts writes again")]
    public async Task DeadLetterStorageAcceptsWrites()
    {
        if (!_deadLetterStorageRefusing) return;

        foreach (var database in Store.Databases)
        {
            await executeAsync(database,
                $"alter table {database.Schema}.wolverine_dead_letters drop constraint if exists {RefusalConstraint}");
        }

        _deadLetterStorageRefusing = false;
    }

    private static async Task executeAsync(RigDatabase database, string sql)
    {
        await using var conn = database.Connect();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Waiting and interrupting

    [When("the batch handler has named the poison item")]
    public async Task ThePoisonItemHasBeenNamed()
    {
        var named = await pollAsync(() => Task.FromResult(BatchItemScript.PoisonThrows > 0), 30.Seconds());
        if (!named) throw new SpecCriticalException("No batch ever threw ApplyItemException; the rest of the scenario depends on it");
    }

    [When("{seconds} second(s) pass")]
    public Task SecondsPass(int seconds) => Task.Delay(seconds.Seconds());

    [When("every item expected to be handled has committed its effect")]
    public async Task EveryHandledItemHasCommitted()
    {
        var handled = _expected.Where(x => x.Value == Outcome.Handled).Select(x => x.Key).ToArray();
        var committed = await pollAsync(async () =>
        {
            var snapshot = await Store.SnapshotAsync();
            return handled.All(id => snapshot.Ledger.Any(x => x.MessageId == id));
        }, SettleTimeout);

        if (!committed) throw new SpecCriticalException("Not every item committed its effect; the rest of the scenario depends on it");
    }

    /// <summary>
    /// A crash in a test process cannot kill the crashed node's handler, which sits on its gate until the scenario
    /// ends. It never commits, so its run is over as far as the checker is concerned: without this, every rerun on
    /// the next node would be reported as overlapping it.
    /// </summary>
    [When("node {node} crashes while its batch is still running")]
    public void NodeCrashesMidBatch(int node)
    {
        NodeCrashes(node);

        var name = $"node {node}";
        foreach (var execution in Script.Log.Where(x => x.Node == name && x.FinishedAt == null))
        {
            execution.Finish("abandoned by the crash");
        }
    }

    [When("the local queue the batches execute on pauses on node {node} for {milliseconds} ms")]
    public async Task TheBatchQueuePauses(int node, int milliseconds)
    {
        var name = Batched.BatchQueueName ?? throw new SpecCriticalException("No batch queue was configured");
        var circuit = Node(node).GetRuntime().Endpoints.AgentForLocalQueue(name) as IListenerCircuit
                      ?? throw new SpecCriticalException($"The batch queue '{name}' cannot be paused");

        await circuit.PauseAsync(milliseconds.Milliseconds());
        SpecLog.Write($"paused {circuit.Endpoint.Uri}; status {circuit.Status}");
    }

    [When("the listener the items arrive on pauses on node {node} for {milliseconds} ms")]
    public async Task TheItemListenerPauses(int node, int milliseconds)
    {
        var runtime = Node(node).GetRuntime();

        // A durable local queue is not a listening agent, but it is a listener circuit
        IListenerCircuit listener = (IListenerCircuit?)itemListener(runtime)
                                    ?? runtime.Endpoints.AgentForLocalQueue(QueueName) as IListenerCircuit
                                    ?? throw new SpecCriticalException($"No listener for {QueueName}");

        await listener.PauseAsync(milliseconds.Milliseconds());
        SpecLog.Write($"paused {listener.Endpoint.Uri}; status {listener.Status}");
    }

    private IListeningAgent? itemListener(WolverineRuntime runtime) =>
        runtime.Endpoints.ActiveListeners().FirstOrDefault(x => x.Uri.ToString().Contains(QueueName));

    // ---------------------------------------------------------------------------------------------------------
    // Checks only a batch needs

    /// <summary>
    /// F8. Every member counted into a batching pipeline is released at its batch's terminal. A count left behind
    /// inflates the item listener's QueueCount, which back-pressure reads, and can hold it TooBusy forever.
    /// </summary>
    [Then("no batch members are left pending")]
    public void NoBatchMembersArePending()
    {
        var leftovers = new List<string>();
        for (var i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i] is not { } host) continue;

            var runtime = host.GetRuntime();
            var pipeline = runtime.BatchingPendingCounts.PendingForBatchedMessage<RigItem>();
            if (pipeline != 0) leftovers.Add($"node {i + 1}: {pipeline} member(s) pending in the RigItem pipeline");

            if (itemListener(runtime) is { } listener)
            {
                var pending = runtime.BatchingPendingCounts.PendingFor(listener.Uri);
                if (pending != 0) leftovers.Add($"node {i + 1}: {pending} member(s) pending against {listener.Uri}");
                if (listener.Status != ListeningStatus.Accepting)
                {
                    leftovers.Add($"node {i + 1}: {listener.Uri} is {listener.Status} with QueueCount {listener.QueueCount}");
                }
            }
        }

        SpecAssert.Check("batch members left pending", leftovers.Count == 0 ? "none" : leftovers.Join("; "), "none");
    }

    /// <summary>
    /// F1. A failure to settle a batch member is retried forever in the background, so it never shows up in the
    /// store; the logs are the only place it can be seen. A NullReferenceException there is a member envelope the
    /// receiver could not even address.
    /// </summary>
    [Then("no errors were logged settling batch members")]
    public void NoErrorsSettlingBatchMembers()
    {
        var entries = RigLogCapture.Entries;
        foreach (var group in entries.GroupBy(x => $"{x.Category} {x.Exception?.GetType().Name}"))
        {
            SpecLog.Write($"{group.Count()} error log(s): {group.Key}");

            // The whole first one, inner exceptions and stack included, for triage
            var first = group.First();
            _output.WriteLine($"FIRST OF {group.Count()}: {first.Level} {first.Category}: {first.Message}{Environment.NewLine}{first.Exception}");
        }

        var nullReferences = entries.Where(x => x.Chain().Any(e => e is NullReferenceException)).ToArray();
        SpecAssert.Check("NullReferenceExceptions logged",
            nullReferences.Length == 0 ? "none" : $"{nullReferences.Length}, first: {nullReferences[0]}", "none");
    }

    /// <summary>Strict, unlike the invariant: the restart in these scenarios came after every item had settled.</summary>
    [Then("no item's effect committed more than once")]
    public async Task NoItemCommittedTwice()
    {
        var snapshot = await Store.SnapshotAsync();
        var duplicates = new Invariants(snapshot, _expected).DuplicateEffects();
        SpecAssert.Check("items whose effect committed more than once",
            duplicates.Count == 0 ? "none" : duplicates.Select(x => $"{x.id} x{x.effects}").Join(", "), "none");
    }

    /// <summary>Called from the compliance base's DisposeAsync: never leave the dead letter table refusing writes.</summary>
    public async Task RestoreBatchingStateAsync()
    {
        try
        {
            await DeadLetterStorageAcceptsWrites();
        }
        catch (Exception e)
        {
            _output.WriteLine($"Could not restore dead letter storage: {e.Message}");
        }

        BatchItemScript.Reset();
    }
}
