using System.Collections.Concurrent;
using System.Diagnostics;
using Bobcat;
using Bobcat.Engine;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace DurableComplianceRig.Infrastructure;

/// <summary>
/// A store whose nodes can be killed one at a time: the node loses its database for good, as a dead process would.
/// </summary>
public interface IKillableStore
{
    /// <summary>Before any node starts: give each node a database identity of its own.</summary>
    Task PrepareNodesAsync();

    /// <summary>Cut every database session the node has and refuse it new ones.</summary>
    Task KillNodeAsync(int node);

    Task ReviveAllAsync();
}

/// <summary>A store that can stage a database blip on exactly one inbox row.</summary>
public interface IInboxRowBlocker
{
    /// <summary>
    /// Lock the row and keep cutting every session that waits on it until the returned handle is disposed.
    /// <paramref name="cut" /> hears the statement of every session cut.
    /// </summary>
    Task<IAsyncDisposable> BlockInboxRowAsync(Guid id, Action<string> cut);
}

/// <summary>One read of an inbox row, bracketed by when the read started and finished.</summary>
public record RowSample(DateTimeOffset From, DateTimeOffset To, string Status, int OwnerId);

/// <summary>
/// Node lifecycle vocabulary: graceful stops, kills, listener pauses and rebuilds, a database blip on one row, and a
/// watch on one inbox row that says whether it was ever released underneath its running handler.
/// </summary>
public sealed partial class DurableSteps
{
    private readonly List<Task> _dyingNodes = [];
    private readonly ConcurrentQueue<RowSample> _rowSamples = new();
    private CancellationTokenSource? _rowWatch;
    private Task? _rowWatching;

    /// <summary>Not a step. Called before the specification's first step, while nothing has started.</summary>
    public async Task PrepareLifecycleAsync()
    {
        if (Store is IKillableStore killable) await killable.PrepareNodesAsync();
    }

    /// <summary>
    /// Not a step. A killed node's host is still shutting itself down in the background, against a database that
    /// refuses it; wait for that before the next scenario starts, then give every node its login back.
    /// </summary>
    public async Task BuryKilledNodesAsync()
    {
        await stopRowWatchAsync();

        // Opens every gate, so a killed node's held handlers give up rather than hold its shutdown for DrainTimeout
        Script.Reset();

        try
        {
            await Task.WhenAll(_dyingNodes).WaitAsync(90.Seconds());
        }
        catch (Exception e)
        {
            _output.WriteLine($"A killed node did not finish shutting down: {e.Message}");
        }

        if (Store is IKillableStore killable) await killable.ReviveAllAsync();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Arrangement

    [Given("every listener runs {count} messages at a time")]
    public void ListenersRunInParallel(int count)
    {
        ConfigureNodes(opts =>
        {
            opts.Policies.AllListeners(x => x.MaximumParallelMessages(count));
            opts.Policies.AllLocalQueues(x => x.MaximumParallelMessages(count));
        });
    }

    /// <summary>The production defaults are tens of seconds to a minute; a scenario would spend its life waiting.</summary>
    [Given("the cluster notices a dead node, a released row and a due retry within seconds")]
    public void TheClusterReactsWithinSeconds()
    {
        ConfigureNodes(opts =>
        {
            opts.Durability.CheckAssignmentPeriod = 1.Seconds();
            opts.Durability.HealthCheckPollingTime = 1.Seconds();
            opts.Durability.FirstHealthCheckExecution = 1.Seconds();
            opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
            opts.Durability.StaleNodeTimeout = 5.Seconds();
            opts.Durability.OrphanedMessageSweepPollingTime = 1.Seconds();
            opts.Durability.ScheduledJobFirstExecution = 250.Milliseconds();
            opts.Durability.ScheduledJobPollingTime = 1.Seconds();
        });
    }

    [Given("the drain timeout is {milliseconds} ms")]
    public void TheDrainTimeoutIs(int milliseconds) =>
        ConfigureNodes(opts => opts.Durability.DrainTimeout = milliseconds.Milliseconds());

    [Given("the inbox stale time is {seconds} s")]
    public void TheInboxStaleTimeIs(int seconds) =>
        ConfigureNodes(opts => opts.Durability.InboxStaleTime = seconds.Seconds());

    [Given("a failure of message {number} requeues it and pauses its listener for {seconds} s")]
    public void AFailureRequeuesAndPauses(int number, int seconds)
    {
        var id = MessageNumbered(number).ToString();
        ConfigureNodes(opts =>
            opts.OnException<RigFailure>(e => e.Message.Contains(id)).Requeue().AndPauseProcessing(seconds.Seconds()));
    }

    [Given("a failure of message {number} is retried inline after {milliseconds} ms, then scheduled for a retry in {seconds} s")]
    public void AFailureIsRetriedInlineThenScheduled(int number, int milliseconds, int seconds)
    {
        var id = MessageNumbered(number).ToString();
        ConfigureNodes(opts => opts.OnException<RigFailure>(e => e.Message.Contains(id))
            .RetryWithCooldown(milliseconds.Milliseconds()).Then.ScheduleRetry(seconds.Seconds()));
    }

    [Given("a failure of message {number} is scheduled for a retry in {seconds} s")]
    public void AFailureIsScheduledForRetry(int number, int seconds)
    {
        var id = MessageNumbered(number).ToString();
        ConfigureNodes(opts => opts.OnException<RigFailure>(e => e.Message.Contains(id)).ScheduleRetry(seconds.Seconds()));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Sending and waiting

    [When("message {number} is sent from node {node}")]
    public async Task TheMessageIsSent(int number, int node)
    {
        await Node(node).MessageBus().PublishAsync(new RigMessage(MessageNumbered(number)));
    }

    [When("attempt {attempt} of message {number} has started")]
    public async Task AttemptHasStarted(int attempt, int number)
    {
        var id = MessageNumbered(number);
        var started = await pollAsync(() => Task.FromResult(Script.ExecutionsOf(id) >= attempt), 60.Seconds());
        if (!started) throw new SpecCriticalException($"Attempt {attempt} of message {number} never started");
    }

    [When("up to {seconds} s pass while message {number} might run again")]
    public async Task UpToSecondsPassForARerun(int seconds, int number)
    {
        var id = MessageNumbered(number);
        var before = Script.ExecutionsOf(id);
        var rerun = await pollAsync(() => Task.FromResult(Script.ExecutionsOf(id) > before), seconds.Seconds());
        narrate(rerun
            ? $"message {number} ran again: {Script.Log.Where(x => x.MessageId == id).Select(x => x.ToString()).Join("; ")}"
            : $"message {number} did not run again within {seconds}s");
    }

    [When("{seconds} s pass")]
    public Task SecondsPass(int seconds) => Task.Delay(seconds.Seconds());

    // ---------------------------------------------------------------------------------------------------------
    // Stopping and killing nodes

    /// <summary>
    /// A graceful stop whose drain outlasts its in-flight work. It does not relax the duplicate rule: a node that
    /// finishes what it holds and hands back the rest has no reason to run anything twice.
    /// </summary>
    [When("node {node} stops gracefully, and gate {gate} opens {milliseconds} ms into its drain")]
    public async Task NodeStopsWhileGateOpens(int node, string gate, int milliseconds)
    {
        var host = Node(node);
        var stopwatch = Stopwatch.StartNew();
        var stopping = host.StopAsync();

        await Task.Delay(milliseconds.Milliseconds());
        Script.OpenGate(gate);

        await stopping;
        host.Dispose();
        _nodes[node - 1] = null;
        narrate($"node {node} stopped in {stopwatch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// A real shutdown: StopAsync returns, then the process exits and whatever it was still running dies with it.
    /// </summary>
    [When("node {node} shuts down gracefully, then its process exits")]
    public async Task NodeShutsDownAndExits(int node)
    {
        var host = Node(node);
        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync();
        narrate($"node {node} stopped in {stopwatch.ElapsedMilliseconds} ms; its process exits now");

        await killAsync(node, "exited with its process");
    }

    [When("node {node} is killed: its database sessions are cut and it never finishes the work it holds")]
    public Task NodeIsKilled(int node) => killAsync(node, "killed with its node");

    [When("the node running message {number} is killed")]
    public async Task<int> TheNodeRunningMessageIsKilled(int number)
    {
        var node = nodeRunning(number);
        await killAsync(node, "killed with its node");
        return node;
    }

    [When("the node running message {number} stops gracefully, and gate {gate} opens {milliseconds} ms into its drain")]
    public async Task<int> TheNodeRunningMessageStopsWhileGateOpens(int number, string gate, int milliseconds)
    {
        var node = nodeRunning(number);
        await NodeStopsWhileGateOpens(node, gate, milliseconds);
        return node;
    }

    [When("the node running message {number} shuts down gracefully, then its process exits")]
    public async Task<int> TheNodeRunningMessageShutsDownAndExits(int number)
    {
        var node = nodeRunning(number);
        await NodeShutsDownAndExits(node);
        return node;
    }

    private int nodeRunning(int number)
    {
        var id = MessageNumbered(number);
        var running = Script.Log.LastOrDefault(x => x.MessageId == id && x.FinishedAt == null)
                      ?? throw new SpecCriticalException($"Message {number} is not running anywhere");
        var node = int.Parse(running.Node.Split(' ').Last());
        narrate($"message {number} is running on node {node}");
        return node;
    }

    private async Task killAsync(int node, string outcome)
    {
        var host = _nodes.ElementAtOrDefault(node - 1);
        _somethingWasInterrupted = true;
        _nodes[node - 1] = null;

        if (Store is IKillableStore killable)
        {
            await killable.KillNodeAsync(node);
        }
        else
        {
            narrate($"{Store} cannot cut one node's database sessions, so node {node} is only disposed -- which drains it gracefully");
        }

        var name = $"node {node}";
        foreach (var execution in Script.Log.Where(x => x.Node == name && x.FinishedAt == null))
        {
            execution.Finish(outcome);
            narrate($"{execution}");
        }

        if (host == null) return;

        // The host still has to let go of its broker connections and timers. It does that in the background,
        // against a database that refuses it, while the survivors carry on
        _dyingNodes.Add(Task.Run(async () =>
        {
            try
            {
                await host.StopAsync();
            }
            catch (Exception e)
            {
                _output.WriteLine($"killed node {node}: {e.Message}");
            }

            try
            {
                host.Dispose();
            }
            catch (Exception e)
            {
                _output.WriteLine($"killed node {node}: {e.Message}");
            }
        }));

        narrate($"node {node} {outcome}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Listener disruptions

    [When("the listener on node {node} is rebuilt")]
    public async Task TheListenerIsRebuilt(int node)
    {
        var circuit = listenerOn(node);
        var stopwatch = Stopwatch.StartNew();
        await circuit.RestartAsync(force: true);
        narrate($"{circuit.Endpoint.Uri} on node {node} rebuilt in {stopwatch.ElapsedMilliseconds} ms, now {circuit.Status}");
    }

    [When("the listener on node {node} pauses for {seconds} s")]
    public async Task TheListenerPauses(int node, int seconds)
    {
        var circuit = listenerOn(node);
        await circuit.PauseAsync(seconds.Seconds());
        narrate($"{circuit.Endpoint.Uri} on node {node} is {circuit.Status}");
    }

    /// <summary>A pause that does not wait for running handlers, as one raised from inside the handler pipeline.</summary>
    [When("the listener on node {node} pauses for {seconds} s without waiting for its running handlers")]
    public async Task TheListenerPausesWithoutWaiting(int node, int seconds)
    {
        var circuit = listenerOn(node);
        await circuit.PauseAsync(seconds.Seconds());
        narrate($"{circuit.Endpoint.Uri} on node {node} is {circuit.Status}");
    }

    /// <summary>A pause that latches and waits for running handlers first, as the circuit breaker's.</summary>
    [When("the listener on node {node} pauses for {seconds} s after draining its running handlers")]
    public async Task TheListenerPausesAfterDraining(int node, int seconds)
    {
        var circuit = listenerOn(node);
        var stopwatch = Stopwatch.StartNew();
        await circuit.PauseWithDrainAsync(seconds.Seconds());
        narrate($"{circuit.Endpoint.Uri} on node {node} is {circuit.Status} after {stopwatch.ElapsedMilliseconds} ms");
    }

    /// <summary>The first half of a rebuild: latch, drain, stop -- and nothing else.</summary>
    [When("the listener on node {node} stops and drains")]
    public async Task TheListenerStopsAndDrains(int node)
    {
        var circuit = listenerOn(node);
        var stopwatch = Stopwatch.StartNew();
        if (circuit is IListeningAgent agent)
        {
            await agent.StopAndDrainAsync();
        }
        else
        {
            // A local queue has no stop of its own; its pause latches and drains the same way
            await circuit.PauseWithDrainAsync(1.Hours());
        }

        narrate($"{circuit.Endpoint.Uri} on node {node} is {circuit.Status} after {stopwatch.ElapsedMilliseconds} ms");
    }

    /// <summary>The second half of a rebuild.</summary>
    [When("the listener on node {node} starts")]
    public async Task TheListenerStarts(int node)
    {
        var circuit = listenerOn(node);
        await circuit.StartAsync();
        narrate($"{circuit.Endpoint.Uri} on node {node} is {circuit.Status}");
    }

    private IListenerCircuit listenerOn(int node)
    {
        var runtime = Node(node).GetRuntime();
        var databaseQueueName = QueueName.Replace('-', '_');

        var listener = runtime.Endpoints.ActiveListeners().FirstOrDefault(x =>
            x.Uri.ToString().Contains(QueueName) || x.Uri.ToString().Contains(databaseQueueName));
        if (listener != null) return listener;

        return runtime.Endpoints.AgentForLocalQueue(QueueName) as IListenerCircuit
               ?? throw new SpecCriticalException($"Node {node} has no listener for {QueueName}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // A database blip on one row

    [When("the database cuts every write to message {number}'s inbox row from its first failure until {milliseconds} ms after its second attempt starts")]
    public async Task TheDatabaseCutsWritesToTheRow(int number, int milliseconds)
    {
        var id = MessageNumbered(number);
        if (Store is not IInboxRowBlocker blocker)
        {
            throw new SpecCriticalException($"{Store} cannot block a single inbox row");
        }

        var failed = await pollAsync(
            () => Task.FromResult(Script.Log.Any(x => x.MessageId == id && x.Attempt == 1 && x.FinishedAt != null)),
            30.Seconds());
        if (!failed) throw new SpecCriticalException($"Message {number}'s first attempt never finished");

        var cuts = new ConcurrentQueue<string>();
        await using (await blocker.BlockInboxRowAsync(id, cuts.Enqueue))
        {
            var started = await pollAsync(() => Task.FromResult(Script.ExecutionsOf(id) >= 2), 30.Seconds());
            if (!started) throw new SpecCriticalException($"Message {number}'s second attempt never started");

            await Task.Delay(milliseconds.Milliseconds());
        }

        narrate(cuts.IsEmpty
            ? "no session waited on the row while it was blocked"
            : $"{cuts.Count} session(s) cut while waiting on the row: {cuts.Distinct().Join(" | ")}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Watching one row

    [When("message {number}'s inbox row is watched")]
    public void TheInboxRowIsWatched(int number)
    {
        var id = MessageNumbered(number);
        var database = Store.Databases[0];
        _rowWatch = new CancellationTokenSource();
        var token = _rowWatch.Token;

        _rowWatching = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var conn = database.Connect();
                    await conn.OpenAsync(token);
                    while (!token.IsCancellationRequested)
                    {
                        var from = DateTimeOffset.UtcNow;
                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText =
                            $"select status, owner_id from {database.Schema}.wolverine_incoming_envelopes where id = @id";
                        var parameter = cmd.CreateParameter();
                        parameter.ParameterName = "id";
                        parameter.Value = id;
                        cmd.Parameters.Add(parameter);

                        await using (var reader = await cmd.ExecuteReaderAsync(token))
                        {
                            if (await reader.ReadAsync(token))
                            {
                                _rowSamples.Enqueue(new RowSample(from, DateTimeOffset.UtcNow, reader.GetString(0),
                                    reader.GetInt32(1)));
                            }
                        }

                        await Task.Delay(50, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // The watcher never decides anything by failing; try again
                    await Task.Delay(100, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }

    [Then("message {number}'s inbox row stayed owned while its handler was running")]
    public async Task TheRowStayedOwnedWhileItsHandlerRan(int number)
    {
        await stopRowWatchAsync();
        var id = MessageNumbered(number);
        var runs = Script.Log.Where(x => x.MessageId == id).ToArray();

        // Only a read that started and finished inside a run counts: the row was certainly released under it
        var released = _rowSamples
            .Where(x => x is { Status: "Incoming", OwnerId: 0 })
            .Select(sample => (sample, run: runs.FirstOrDefault(r =>
                r.StartedAt <= sample.From && (r.FinishedAt ?? DateTimeOffset.MaxValue) >= sample.To)))
            .Where(x => x.run != null)
            .ToArray();

        narrate($"{_rowSamples.Count} reads of the row; owners seen: {_rowSamples.Select(x => $"{x.Status}/{x.OwnerId}").Distinct().Join(", ")}");

        SpecAssert.Check("reads of the row released (owner 0) while its handler ran",
            released.Length == 0
                ? "none"
                : $"{released.Length}, first at {released[0].sample.From:HH:mm:ss.fff} under {released[0].run}",
            "none");
    }

    /// <summary>A step log, and the test output too: Bobcat renders step logs only for a failing specification.</summary>
    private void narrate(string text)
    {
        SpecLog.Write(text);
        _output.WriteLine(text);
    }

    private async Task stopRowWatchAsync()
    {
        if (_rowWatch == null) return;
        await _rowWatch.CancelAsync();
        if (_rowWatching != null)
        {
            try
            {
#pragma warning disable VSTHRD003 // started by this object, stopped here
                await _rowWatching;
#pragma warning restore VSTHRD003
            }
            catch (Exception)
            {
                // Already stopped
            }
        }

        _rowWatch.Dispose();
        _rowWatch = null;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Stricter checks

    [Then("message {number} ran exactly once")]
    public void MessageRanOnce(int number)
    {
        var id = MessageNumbered(number);
        var runs = Script.Log.Where(x => x.MessageId == id).ToArray();
        if (runs.Length > 1) narrate(runs.Select(x => x.ToString()).Join("; "));
        SpecAssert.Check($"runs of message {number}", runs.Length, 1);
    }

    [Then("attempt {attempt} of message {number} started within {seconds} s of the attempt before it")]
    public void AttemptStartedWithin(int attempt, int number, int seconds)
    {
        var id = MessageNumbered(number);
        var runs = Script.Log.Where(x => x.MessageId == id).OrderBy(x => x.Attempt).ToArray();
        var before = runs.FirstOrDefault(x => x.Attempt == attempt - 1);
        var after = runs.FirstOrDefault(x => x.Attempt == attempt);
        if (before == null || after == null)
        {
            SpecAssert.Fact(false, $"message {number} has no attempts {attempt - 1} and {attempt}");
            return;
        }

        var gap = after.StartedAt - before.StartedAt;
        narrate($"attempt {attempt} of message {number} started {gap.TotalMilliseconds:0} ms after attempt {attempt - 1}");
        SpecAssert.Check($"attempt {attempt} started within {seconds}s", gap <= seconds.Seconds(), true);
    }

    [Then("no message ran more than once")]
    public void NoMessageRanTwice()
    {
        var twice = Script.Log.GroupBy(x => x.MessageId).Where(x => x.Count() > 1)
            .Select(x => $"{x.Key} x{x.Count()}").ToArray();
        SpecAssert.Check("messages run more than once", twice.Length == 0 ? "none" : twice.Join(", "), "none");
    }
}
