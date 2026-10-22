using Bobcat;
using Bobcat.Engine;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;

namespace DurableComplianceRig.Infrastructure;

/// <summary>
/// The durable compliance rig's vocabulary. A step helper has to be public and callable from the interceptor Bobcat
/// generates, so the steps live here, on a non-generic class every compliance base holds as <c>the</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Arrange the messages, then send them.</b> A message's script has to be in place before it can arrive, so
/// <see cref="MessagesToSend" /> mints them, the <c>Given</c> steps script them by number, and
/// <see cref="TheMessagesAreSent" /> sends them all.
/// </para>
/// <para>
/// <b>Every invariant is its own check.</b> <see cref="TheDurableInvariantsHold" /> waits for the run to settle and
/// then judges lost messages, duplicate effects, concurrent runs, dead letters, unsettled inbox rows and outbox
/// leftovers separately, so one failing spec reports everything that went wrong.
/// </para>
/// </remarks>
public sealed partial class DurableSteps : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<IHost?> _nodes = [];
    private readonly List<Guid> _messages = [];
    private readonly Dictionary<Guid, Outcome> _expected = [];
    private readonly List<Action<WolverineOptions>> _nodeConfiguration = [];
    private bool _somethingWasInterrupted;

    public DurableSteps(StoreProfile store, TransportProfile transport, ITestOutputHelper output)
    {
        Store = store;
        Transport = transport;
        _output = output;
        Script.Reset();
    }

    public StoreProfile Store { get; }
    public TransportProfile Transport { get; }

    /// <summary>
    /// Unique per spec. A broker queue is never reused, so nothing has to be purged at startup -- an SQS purge
    /// completes asynchronously and deletes messages sent in the minute after it, which made the first rig runs
    /// lose a whole receive batch on SQS.
    /// </summary>
    public string QueueName { get; } = $"rig-{Guid.NewGuid().ToString("N")[..8]}{RigEnvironment.QueueSuffix}";

    /// <summary>Solo for one node; a cluster scenario sets Balanced before its nodes start.</summary>
    public DurabilityMode Mode { get; set; } = DurabilityMode.Solo;

    /// <summary>How long <see cref="TheDurableInvariantsHold" /> waits for every message to reach its outcome.</summary>
    public TimeSpan SettleTimeout { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("RIG_SETTLE_SECONDS"), out var seconds) ? seconds.Seconds() : 60.Seconds();

    /// <summary>How long to keep watching after the run looks settled, for a late duplicate or a late release.</summary>
    public TimeSpan QuietPeriod { get; set; } = 2.Seconds();

    public IHost Node(int number) =>
        _nodes.ElementAtOrDefault(number - 1)
        ?? throw new SpecCriticalException($"Node {number} is not running; {_nodes.Count} node(s) have started");

    public IReadOnlyList<Guid> Messages => _messages;

    public Guid MessageNumbered(int number) =>
        _messages.ElementAtOrDefault(number - 1) is var id && id != Guid.Empty
            ? id
            : throw new SpecCriticalException($"There is no message {number}; {_messages.Count} were arranged");

    /// <summary>Extra configuration for every node started after this call. Not a step: it renders nothing.</summary>
    public void ConfigureNodes(Action<WolverineOptions> configure) => _nodeConfiguration.Add(configure);

    // ---------------------------------------------------------------------------------------------------------
    // Nodes

    [Given("a node starts")]
    public async Task<int> ANodeStarts()
    {
        var number = _nodes.Count + 1;
        var name = $"node {number}";

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "durable-rig";
                opts.Durability.Mode = Mode;
                opts.Discovery.DisableConventionalDiscovery();

                Store.ConfigurePersistence(opts, Transport is DatabaseQueueTransport);
                Transport.Configure(opts, Store, QueueName);
                opts.Services.AddRigNode(name);

                foreach (var configure in _nodeConfiguration) configure(opts);
            }).StartAsync();

        _nodes.Add(host);

        if (number == 1)
        {
            // Wipe whatever an earlier run left in the store before anything is sent. Broker queues need no purge:
            // every spec gets a queue of its own (QueueName)
            await host.ResetResourceState();
            await Store.EnsureLedgerAsync();
            foreach (var database in Store.Databases) await database.ClearLedgerAsync();
            Script.Reset();
        }

        SpecLog.Write($"{name} started over {Transport} with {Store}, {Mode}");
        return number;
    }

    [When("node {node} stops gracefully")]
    public async Task NodeStops(int node)
    {
        var host = Node(node);
        await host.StopAsync();
        host.Dispose();
        _nodes[node - 1] = null;
        _somethingWasInterrupted = true;
    }

    /// <summary>
    /// NOT a crash. <c>IHost.Dispose</c> runs <c>WolverineRuntime.DisposeAsync</c> -> <c>StopAsync</c>, which drains,
    /// releases every row the node owns and deletes its node record -- a graceful stop by another road. For a real
    /// kill use <see cref="NodeIsKilled" /> on a store that implements <see cref="IKillableStore" />.
    /// </summary>
    [When("node {node} is disposed, which stops it gracefully")]
    public void NodeIsDisposed(int node)
    {
        var host = Node(node);
        host.Dispose();
        _nodes[node - 1] = null;
        _somethingWasInterrupted = true;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Messages

    [Given("{count} messages to send")]
    public void MessagesToSend(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            _messages.Add(id);
            _expected[id] = Outcome.Handled;
        }
    }

    [Given("message {number} fails the first {failures} time(s)")]
    public void MessageFailsFirst(int number, int failures)
    {
        var steps = Enumerable.Repeat(Step.Throw, failures).Append(Step.Succeed).ToArray();
        Script.Set(MessageNumbered(number), steps);
    }

    [Given("message {number} fails every time and should be dead lettered")]
    public void MessageAlwaysFails(int number)
    {
        var id = MessageNumbered(number);
        Script.Set(id, Step.Throw);
        _expected[id] = Outcome.DeadLettered;
    }

    [Given("message {number} fails after writing its effect, before the commit, the first {failures} time(s)")]
    public void MessageFailsBeforeCommit(int number, int failures)
    {
        var steps = Enumerable.Repeat(Step.ThrowAfterLedgerWrite, failures).Append(Step.Succeed).ToArray();
        Script.Set(MessageNumbered(number), steps);
    }

    [Given("message {number} takes {milliseconds} ms to handle")]
    public void MessageIsSlow(int number, int milliseconds) =>
        Script.Set(MessageNumbered(number), Step.Wait(milliseconds.Milliseconds()));

    [Given("message {number} waits for gate {gate}")]
    public void MessageWaitsForGate(int number, string gate) =>
        Script.Set(MessageNumbered(number), Step.WaitForGate(gate), Step.Succeed);

    [When("gate {gate} opens")]
    public void GateOpens(string gate) => Script.OpenGate(gate);

    [When("the messages are sent from node {node}")]
    public async Task TheMessagesAreSent(int node)
    {
        var bus = Node(node).MessageBus();
        foreach (var id in _messages) await bus.PublishAsync(new RigMessage(id));
    }

    [When("the messages are sent from node {node} as batch items")]
    public async Task TheMessagesAreSentAsItems(int node)
    {
        var bus = Node(node).MessageBus();
        foreach (var id in _messages) await bus.PublishAsync(new RigItem(id));
    }

    [When("message {number} has started on some node")]
    public async Task MessageHasStarted(int number)
    {
        var id = MessageNumbered(number);
        var started = await pollAsync(() => Task.FromResult(Script.ExecutionsOf(id) > 0), 30.Seconds());
        if (!started) throw new SpecCriticalException($"Message {number} never started; the rest of the scenario depends on it");
    }

    [When("message {number} has committed its effect")]
    public async Task MessageHasCommitted(int number)
    {
        var id = MessageNumbered(number);
        var committed = await pollAsync(async () => (await Store.SnapshotAsync()).Ledger.Any(x => x.MessageId == id),
            30.Seconds());
        if (!committed) throw new SpecCriticalException($"Message {number} never committed; the rest of the scenario depends on it");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Invariants

    [Then("the durable invariants hold")]
    public async Task TheDurableInvariantsHold()
    {
        var invariants = await settleAsync();

        // Guards every row-level check below: inbox, dead letter and outbox rows are matched to messages by id
        SpecAssert.Check("envelopes not carrying their message's id",
            describe(invariants.Snapshot.Executions.Where(x => x.EnvelopeId.HasValue && x.EnvelopeId != x.MessageId)
                .Select(x => $"{x.MessageId} rode {x.EnvelopeId}").Distinct()), "none");

        SpecAssert.Check("lost messages", describe(invariants.Lost()), "none");

        // Under at-least-once a duplicate is legitimate only after a crash or a release
        var duplicates = invariants.DuplicateEffects();
        if (Store.Semantics == EffectSemantics.ExactlyOnce || !_somethingWasInterrupted)
        {
            SpecAssert.Check("messages whose effect committed more than once",
                duplicates.Count == 0 ? "none" : duplicates.Select(x => $"{x.id} x{x.effects}").Join(", "), "none");
        }
        else if (duplicates.Count > 0)
        {
            SpecLog.Write($"{duplicates.Count} duplicate effect(s), allowed under at-least-once after an interruption");
        }

        SpecAssert.Check("effects committed for messages that should not have any",
            describe(invariants.UnexpectedEffects()), "none");
        SpecAssert.Check("dead letter mismatches", describe(invariants.DeadLetterMismatches()), "none");
        SpecAssert.Check("handlers running the same message at the same time",
            describe(invariants.ConcurrentExecutions()), "none");
        SpecAssert.Check("inbox rows left unsettled",
            describe(invariants.UnsettledInboxRows().Select(x =>
                $"{x.Id} {x.Status} owner={x.OwnerId} attempts={x.Attempts} at {x.ReceivedAt} in {x.Database}")),
            "none");
        SpecAssert.Check("rows left in the outbox",
            describe(invariants.OutboxLeftovers().Select(x => $"{x.Id} to {x.Destination} owner={x.OwnerId}")), "none");
    }

    private async Task<Invariants> settleAsync()
    {
        var settled = await pollAsync(async () => new Invariants(await Store.SnapshotAsync(), _expected).IsSettled(),
            SettleTimeout);

        if (settled)
        {
            await Task.Delay(QuietPeriod);
        }
        else
        {
            SpecLog.Write($"the run did not settle within {SettleTimeout.TotalSeconds:0}s; judging it as it stands");
        }

        var invariants = new Invariants(await Store.SnapshotAsync(), _expected);
        foreach (var execution in invariants.Snapshot.Executions) _output.WriteLine(execution.ToString());

        while (Stores.WatchedEnvelopeTransaction.EarlyDisposals.TryDequeue(out var disposal))
        {
            SpecLog.Write(disposal);
            _output.WriteLine(disposal);
        }
        return invariants;
    }

    private static async Task<bool> pollAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(250.Milliseconds());
        }

        return await condition();
    }

    private static string describe<T>(IEnumerable<T> items)
    {
        var all = items.Select(x => x!.ToString()!).ToArray();
        return all.Length == 0 ? "none" : all.Join("; ");
    }

    public async ValueTask DisposeAsync()
    {
        Script.Reset();
        foreach (var host in _nodes.OfType<IHost>())
        {
            try
            {
                await host.StopAsync();
            }
            catch (Exception e)
            {
                _output.WriteLine($"Error stopping a node: {e.Message}");
            }

            host.Dispose();
        }
    }
}
