using System.Collections.Concurrent;
using System.Threading.Channels;
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
    ///     Queue a command for its destination's lane, unless equivalent work is already queued or running.
    ///     Never blocks on execution.
    /// </summary>
    public void Enqueue(IAgentCommand command)
    {
        if (_cancellation.IsCancellationRequested) return;

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
    ///     <see cref="ReassignAgent" /> also stops the agent on its previous node, so suppressing one because
    ///     a start is already pending would drop that stop, and the stop commands are not starts at all.
    /// </summary>
    internal static Uri[] StartedAgentsOf(IAgentCommand command) => command switch
    {
        AssignAgent assign => [assign.AgentUri],
        AssignAgents batch => batch.AgentIds,
        _ => []
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
        while (!_cancellation.IsCancellationRequested)
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

            try
            {
                var cascaded = await _executor(command, _cancellation);
                if (cascaded != null)
                {
                    // Route a cascade back through Enqueue rather than executing it here, so it lands in the
                    // lane of the node it actually targets -- e.g. ReassignAgent runs in the new node's lane
                    // and cascades an AssignAgent for that same node.
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
        foreach (var lane in _lanes.Values)
        {
            lane.Queue.Writer.TryComplete();
        }

        foreach (var lane in _lanes.Values)
        {
            try
            {
                var worker = lane.Worker;
                if (worker != null) await worker;
            }
            catch (Exception)
            {
                // Shutting down; a lane that faulted on the way out is not interesting.
            }
        }

        _lanes.Clear();
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
