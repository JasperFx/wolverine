using System.Collections.Concurrent;
using System.Threading.Channels;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Executes the agent commands the leader's assignment evaluation produces, on a <b>persistent lane per
///     destination node</b>: serial within a lane, concurrent across lanes, and never blocking the loop that
///     produced the commands.
///
///     <para>GH-3698. This replaced two earlier shapes, each of which had the same flaw in a different place.
///     Originally the drain was strictly serial across the whole cluster, so a node slow to start its share
///     held up every other node — cluster-wide start concurrency was capped at one node's
///     <c>MaxAgentStartParallelism</c> no matter how many nodes existed. Laning each <i>wave</i> by
///     destination fixed that, but left head-of-line blocking <i>between</i> waves: the pump awaited one
///     wave at a time, so a wave holding an <c>AssignAgents</c> aimed at a node that had just been removed
///     blocked on its <c>30s + chunkSize</c> reply window (or <c>AssignAgent</c>'s 60s acknowledgement) while
///     the re-targets to the surviving nodes queued up behind it and never ran. That regressed seven
///     leadership-takeover tests.</para>
///
///     <para>Lanes are therefore long-lived and independent: a lane wedged on a dead node drains nothing but
///     its own queue, and the agents it was carrying are free to be re-issued to a healthy node on the next
///     evaluation, into a different lane, immediately.</para>
/// </summary>
internal class AgentCommandDispatcher : IAsyncDisposable
{
    // Guid.Empty is the shared lane for every command that names no single destination. Those keep the
    // strictly-serial behaviour the whole drain used to have.
    private static readonly Guid SharedLane = Guid.Empty;

    private readonly Func<IAgentCommand, CancellationToken, Task<AgentCommands?>> _executor;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellation;

    private readonly ConcurrentDictionary<Guid, Lane> _lanes = new();

    // Commands queued or executing right now. An evaluation that re-emits work already in flight must not
    // queue it again -- the pending-assignment ledger only suppresses re-emission for its TTL, and a lane
    // busy with slow starts can outlive that easily.
    private readonly ConcurrentDictionary<IAgentCommand, byte> _queued = new();

    // The agents a start is queued or in flight for, and the node each was sent to. Suppression has to
    // happen at this level as well as per command, because successive evaluations re-chunk the unstarted
    // remainder differently, so two chunks naming overlapping agents are not equal as commands.
    //
    // Keyed on the DESTINATION as well as the agent, deliberately: an agent whose node went away has to be
    // free to move immediately, and a re-target is a different instruction, not a duplicate of the one in
    // flight. Keying on the agent alone strands it for as long as the doomed in-flight copy takes to time
    // out. Same semantics as NodeAgentController's pending-assignment ledger.
    private readonly ConcurrentDictionary<Uri, Guid> _inFlight = new();

    // GH-3852: the agents a reassignment is MOVING, and the node each is moving to. Deliberately separate
    // from _inFlight, because Enqueue must never consult this one: a reassignment carries a stop for the
    // agent's current node, and dropping it because a start is already pending is exactly the two-copies bug
    // StartedAgentsOf's comment guards against. This exists only so the leader's pending-assignment ledger
    // can tell that a move it already dispatched is still executing.
    //
    // Without it a pending reassignment falls back to the ledger's TTL backstop -- 2 x CheckAssignmentPeriod,
    // 60s by default -- which a batch's own reply window trivially outlives (AgentBatchTimeouts.ReplyWindowFor
    // is over 50 minutes at 100 agents), so the leader would resume re-deciding the move long before the
    // command it is waiting on had any chance to finish.
    private readonly ConcurrentDictionary<Uri, Guid> _moving = new();

    // GH-3781: latched by DisposeAsync so a lane stops picking work up. Completing a channel writer does
    // NOT discard what is already buffered -- ReadAsync keeps handing it out -- so without this, shutdown
    // executed every command still queued for a node that had already gone, one reply window at a time.
    private volatile bool _disposing;

    /// <summary>
    ///     How long <see cref="DisposeAsync" /> will wait on a single lane before giving up on it. A lane
    ///     that honours the cancellation token unwinds in microseconds; this only exists so that a future
    ///     non-cancellable await inside a command can never again wedge <c>IHost.StopAsync()</c>. Settable
    ///     for tests.
    /// </summary>
    internal static TimeSpan LaneShutdownTimeout { get; set; } = 5.Seconds();

    internal AgentCommandDispatcher(
        Func<IAgentCommand, CancellationToken, Task<AgentCommands?>> executor,
        ILogger logger,
        CancellationToken cancellation)
    {
        _executor = executor;
        _logger = logger;
        _cancellation = cancellation;
    }

    /// <summary>
    ///     Number of live lanes. Exposed for tests and diagnostics.
    /// </summary>
    internal int LaneCount => _lanes.Count;

    /// <summary>
    ///     The agents a start is currently queued or in flight for. Exposed for tests.
    /// </summary>
    internal IReadOnlyDictionary<Uri, Guid> InFlightAgents => _inFlight;

    /// <summary>
    ///     Is a start for this agent still queued or executing, and if so against which node? This is the
    ///     leader's "confirmed or failed" signal: an entry lives from the moment a command is queued until its
    ///     lane has finished with it, however it finished. GH-3698 — the pending-assignment ledger holds an
    ///     agent on its destination for exactly as long as this says the dispatch is still outstanding, rather
    ///     than for a fixed TTL that a wave of slow starts trivially outlives.
    /// </summary>
    public bool TryFindPendingDestination(Uri agentUri, out Guid nodeId)
        => _inFlight.TryGetValue(agentUri, out nodeId) || _moving.TryGetValue(agentUri, out nodeId);

    /// <summary>
    ///     Queue a command for its destination's lane, unless equivalent work is already queued or running.
    ///     Never blocks on execution.
    /// </summary>
    public void Enqueue(IAgentCommand command)
    {
        if (_cancellation.IsCancellationRequested || _disposing) return;

        var destination = command.DestinationNodeId ?? SharedLane;

        var starting = StartedAgentsOf(command);
        if (starting.Length > 0)
        {
            var fresh = starting
                .Where(uri => !(_inFlight.TryGetValue(uri, out var pending) && pending == destination))
                .ToArray();

            if (fresh.Length == 0)
            {
                return;
            }

            if (fresh.Length != starting.Length)
            {
                // Only a batch can be narrowed to a subset; a single-agent AssignAgent is all-or-nothing and
                // would have been dropped above.
                command = command is AssignAgents batch
                    ? new AssignAgents(batch.Destination, fresh)
                    : command;
            }
        }

        // Command types without value equality fall back to reference equality and are simply never
        // collapsed, which is the old behaviour.
        if (!_queued.TryAdd(command, 0))
        {
            return;
        }

        foreach (var uri in StartedAgentsOf(command)) _inFlight[uri] = destination;

        // Keyed on the node the agents are moving TO, which for a reassignment is not this command's lane --
        // the lane is the SOURCE (see ReassignAgent.DestinationNodeId).
        var moving = MovedAgentsOf(command);
        if (moving != null)
        {
            foreach (var uri in moving.Value.Agents) _moving[uri] = moving.Value.Destination;
        }

        var lane = laneFor(destination);
        if (!lane.Queue.Writer.TryWrite(command))
        {
            // The lane is closed (shutdown). Let go of the claims so nothing is suppressed by a command that
            // will never run.
            release(command, destination);
        }
    }

    /// <summary>
    ///     The agents a command would START somewhere. Deliberately only the first-time placements: a
    ///     <see cref="ReassignAgent" /> or <see cref="ReassignAgents" /> also stops the agents on their
    ///     previous node, so suppressing one because a start is already pending would drop that stop, and
    ///     the stop commands are not starts at all.
    /// </summary>
    internal static Uri[] StartedAgentsOf(IAgentCommand command) => command switch
    {
        AssignAgent assign => [assign.AgentUri],
        AssignAgents batch => batch.AgentIds,
        _ => []
    };

    /// <summary>
    ///     GH-3852: the agents a command is MOVING and the node they are moving to, or null for anything that
    ///     is not a reassignment. Feeds <see cref="_moving" /> only — never <see cref="Enqueue" />'s
    ///     suppression, for the reason given on <see cref="StartedAgentsOf" />.
    /// </summary>
    internal static (Uri[] Agents, Guid Destination)? MovedAgentsOf(IAgentCommand command) => command switch
    {
        ReassignAgent reassign => ([reassign.AgentUri], reassign.ActiveNode.NodeId),
        ReassignAgents batch => (batch.AgentUris, batch.ActiveNode.NodeId),
        _ => null
    };

    private void release(IAgentCommand command, Guid destination)
    {
        _queued.TryRemove(command, out _);

        foreach (var uri in StartedAgentsOf(command))
        {
            // Only clear an entry this command still owns. A re-target queued while this command was running
            // has already overwritten it, and releasing that would let the re-target be issued twice.
            if (_inFlight.TryGetValue(uri, out var owner) && owner == destination)
            {
                _inFlight.TryRemove(uri, out _);
            }
        }

        // Same "only clear what this command still owns" rule, against the move's own destination rather
        // than the lane key.
        var moving = MovedAgentsOf(command);
        if (moving != null)
        {
            foreach (var uri in moving.Value.Agents)
            {
                if (_moving.TryGetValue(uri, out var owner) && owner == moving.Value.Destination)
                {
                    _moving.TryRemove(uri, out _);
                }
            }
        }
    }

    private Lane laneFor(Guid destination)
    {
        // GetOrAdd's factory can run more than once under contention, so the worker is started through a
        // Lazy inside the Lane rather than by the factory itself.
        var lane = _lanes.GetOrAdd(destination, _ => new Lane());
        lane.Start(this, destination);
        return lane;
    }

    private async Task runLaneAsync(Lane lane, Guid destination)
    {
        while (!_cancellation.IsCancellationRequested && !_disposing)
        {
            IAgentCommand command;
            try
            {
                command = await lane.Queue.Reader.ReadAsync(_cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }

            // GH-3781: completing the writer wakes this read with whatever is still buffered, so the
            // shutdown latch has to be re-checked HERE and not only in the loop condition. Anything
            // still queued when the node is going down is work for a cluster this node is leaving.
            if (_disposing)
            {
                release(command, destination);
                return;
            }

            try
            {
                var cascaded = await _executor(command, _cancellation);
                if (cascaded != null)
                {
                    // Route a cascade back through Enqueue rather than executing it here, so it lands in the
                    // lane of the node it actually targets -- e.g. ReassignAgent runs in the source node's
                    // lane (GH-3749) and cascades an AssignAgent that belongs in the destination's lane.
                    foreach (var next in cascaded) Enqueue(next);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A failure must cost this one command and nothing else. Before GH-3698 any exception -- most
                // realistically a TimeoutException from a chunk that outran its reply window -- unwound out
                // of the drain and DISCARDED every command still queued for every other node.
                _logger.LogError(e, "Error trying to execute agent command {Command} against node {NodeId}",
                    command, command.DestinationNodeId);
            }
            finally
            {
                release(command, destination);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;

        foreach (var pair in _lanes)
        {
            pair.Value.Queue.Writer.TryComplete();

            // Abandon whatever is still buffered rather than executing it on the way out. Each of these
            // would otherwise cost its own reply window -- AgentBatchTimeouts.ReplyWindowFor(50) is 25.5
            // minutes -- and they are aimed at a cluster this node is in the middle of leaving.
            while (pair.Value.Queue.Reader.TryRead(out var abandoned))
            {
                release(abandoned, pair.Key);
            }
        }

        foreach (var pair in _lanes)
        {
            try
            {
                var worker = pair.Value.Worker;
                if (worker != null) await worker.WaitAsync(LaneShutdownTimeout);
            }
            catch (TimeoutException)
            {
                // GH-3781: a lane still holding on past the budget must not hold IHost.StopAsync() with it.
                // The whole wedge was one lane parked on a reply from a node that had already gone, with
                // teardownAgentsAsync -- and therefore the node's own deregistration -- queued behind it.
                _logger.LogWarning(
                    "Agent command lane for node {NodeId} did not finish within {Timeout} during shutdown; abandoning it",
                    pair.Key == SharedLane ? null : pair.Key, LaneShutdownTimeout);
            }
            catch (Exception)
            {
                // Shutting down; a lane that faulted on the way out is not interesting.
            }
        }

        _lanes.Clear();

        // Nothing is outstanding once the lanes are gone. Leaving these populated would make
        // TryFindPendingDestination claim a start is still on its way for the rest of the process.
        _queued.Clear();
        _inFlight.Clear();
        _moving.Clear();
    }

    private class Lane
    {
        private Task? _worker;
        private readonly object _gate = new();

        public Channel<IAgentCommand> Queue { get; } =
            Channel.CreateUnbounded<IAgentCommand>(new UnboundedChannelOptions { SingleReader = true });

        public Task? Worker => _worker;

        public void Start(AgentCommandDispatcher parent, Guid destination)
        {
            if (_worker != null) return;

            lock (_gate)
            {
                _worker ??= Task.Run(() => parent.runLaneAsync(this, destination));
            }
        }
    }
}
