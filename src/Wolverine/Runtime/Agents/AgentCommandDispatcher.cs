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
    //
    // The VALUE is the enqueue that owns the entry. Command equality decides whether work is a duplicate,
    // but it cannot serve as the lifecycle identity: an abandoned lane releases its commands while their
    // worker is still parked on one of them, so by the time that worker unwinds an equal command may
    // legitimately be queued again -- a node absent for a sweep or two and then back is enough. Releasing on
    // the command alone would then strip the claims of a live dispatch. See release().
    private readonly ConcurrentDictionary<IAgentCommand, Dispatch> _queued = new();

    private long _ticket;

    /// <summary>
    ///     One enqueue of one command: what travels down a lane's channel, and the identity release() works
    ///     against. Two equal commands queued at different times are different dispatches, and the lane
    ///     records whose work it is -- <see cref="AbandonLane" /> may only let go of its own.
    /// </summary>
    private readonly record struct Dispatch(long Ticket, Lane Lane, IAgentCommand Command);

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

    /// <summary>
    ///     Test-only interleaving seam: null in production, where it costs one null check per point.
    ///
    ///     <para>Three threads meet in this class — the leader's evaluation calling <see cref="Enqueue" />,
    ///     the health check calling <see cref="AbandonLane" />, and a lane's own worker — and the correctness
    ///     of each of them turns on windows a few statements wide, such as the gap between publishing a
    ///     dispatch's claims and publishing the queue entry that owns them. A caller cannot steer those
    ///     windows: they are interior to a single call and measured in nanoseconds. Setting this parks the
    ///     first thread to reach a named point until a test releases it, which makes the interleaving the
    ///     test's to choose rather than the scheduler's.</para>
    ///
    ///     <para>Per instance rather than static like <see cref="LaneShutdownTimeout" />, so tests that use it
    ///     stay independent of each other under a parallel run.</para>
    /// </summary>
    internal Action<string>? Interleave { get; set; }

    // Everything an enqueue does before the queue entry is published -- claims taken, lane resolved.
    internal const string EnqueueClaimed = "enqueue:claimed";

    // The queue entry is published, so the dispatch is visible to AbandonLane, but it is not in the lane yet.
    internal const string EnqueueQueued = "enqueue:queued";

    // The lane is out of the map, its writer is completed and abandonment is latched. Claims not yet let go.
    internal const string AbandonLatched = "abandon:latched";

    // The claims are let go and reported. The lane's token is not cancelled yet.
    internal const string AbandonReleased = "abandon:released";

    // A lane worker has a dispatch in hand and has not yet decided whether to run it.
    internal const string LaneRead = "lane:read";

    // A lane worker's command has returned and its cascade has not been offered to the lane yet.
    internal const string LaneExecuted = "lane:executed";

    private void interleave(string point) => Interleave?.Invoke(point);

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

        // Lane, then claims, then the queue entry. AbandonLane runs on the health-check thread and can land
        // between any two of these, and only this order survives it:
        //
        //   - the lane first, because the entry records which lane owns the dispatch and an entry naming no
        //     lane yet is indistinguishable from one belonging to the lane being abandoned;
        //   - the claims before the entry, because the entry is what makes the dispatch visible to the scan.
        //     Taken the other way round, a scan in between clears the entry, finds no claims and reports
        //     nothing -- and the claims that then land are skipped by every later release() on its
        //     single-shot gate, stranding the agent for the life of the process: suppressed from this
        //     destination by the freshness filter above, and permanently pending to the leader's ledger.
        //
        // Claiming before the entry is safe in both outcomes. For a command that STARTS agents the TryAdd
        // cannot fail, because an equal command already queued would hold all of these agents against this
        // same destination and the freshness filter would have returned. A reassignment can collapse onto an
        // equal command, and then these are the very (agent -> destination) pairs that command already holds,
        // which its own release clears.
        var lane = laneFor(destination);

        foreach (var uri in StartedAgentsOf(command)) _inFlight[uri] = destination;

        // Keyed on the node the agents are moving TO, which for a reassignment is not this command's lane --
        // the lane is the SOURCE (see ReassignAgent.DestinationNodeId).
        var moving = MovedAgentsOf(command);
        if (moving != null)
        {
            foreach (var uri in moving.Value.Agents) _moving[uri] = moving.Value.Destination;
        }

        interleave(EnqueueClaimed);

        // Command types without value equality fall back to reference equality and are simply never
        // collapsed, which is the old behaviour.
        var dispatch = new Dispatch(Interlocked.Increment(ref _ticket), lane, command);
        if (!_queued.TryAdd(command, dispatch))
        {
            return;
        }

        interleave(EnqueueQueued);

        if (!lane.Queue.Writer.TryWrite(dispatch))
        {
            // The lane is closed (shutdown). Let go of the claims so nothing is suppressed by a command that
            // will never run.
            release(dispatch, destination);
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

    /// <summary>
    ///     Let go of the claims a command is holding. <paramref name="released" />, when given, collects the
    ///     agents this call actually cleared, each with the node it was claimed for. Both halves matter: the
    ///     leader ends a pending assignment only where agent and destination agree, so naming a claim this
    ///     call did not clear -- or naming one without its destination -- lands two copies of the agent.
    /// </summary>
    private void release(Dispatch dispatch, Guid destination,
        List<(Uri Agent, Guid Destination)>? released = null)
    {
        var command = dispatch.Command;

        // Single-shot, and against THIS enqueue rather than against anything merely equal to it. AbandonLane
        // releases when the node departs and the lane worker releases again when the abandoned command
        // finally unwinds; without the ticket the second release could clear a claim taken by a re-placement
        // -- or by a later, equal command queued onto a re-created lane -- in the meantime.
        if (!_queued.TryRemove(new KeyValuePair<IAgentCommand, Dispatch>(command, dispatch)))
        {
            return;
        }

        foreach (var uri in StartedAgentsOf(command))
        {
            // Compare-and-remove, so a re-target that has since overwritten this entry keeps it -- releasing
            // that would let the re-target be issued twice -- and two lanes racing cannot both report it.
            //
            // The destination is identity enough here, unlike in _queued above, and only because two live
            // dispatches can never hold the same agent against the SAME node: Enqueue's freshness filter
            // drops an agent already in flight to the destination being asked for, so the second one is never
            // created. For _moving below the same guarantee comes from outside -- Enqueue deliberately does
            // not consult it, and it is the leader's isOutstanding(agent, node) that declines to re-emit a
            // move whose destination already has one pending.
            if (_inFlight.TryRemove(new KeyValuePair<Uri, Guid>(uri, destination)))
            {
                released?.Add((uri, destination));
            }
        }

        // Same rule, against the move's own destination rather than the lane key.
        var moving = MovedAgentsOf(command);
        if (moving != null)
        {
            foreach (var uri in moving.Value.Agents)
            {
                if (_moving.TryRemove(new KeyValuePair<Uri, Guid>(uri, moving.Value.Destination)))
                {
                    released?.Add((uri, moving.Value.Destination));
                }
            }
        }
    }

    /// <summary>
    ///     Give up on every command queued or executing for a node that is no longer a member of the cluster,
    ///     and return the agents whose claims that releases, each with the node it was claimed for.
    ///
    ///     <para>An ungraceful death leaves the dead node's rows in place for up to <c>StaleNodeTimeout</c>,
    ///     so a rebalance can still decide to move agents OFF it. That goes out as a
    ///     <see cref="ReassignAgents" /> in the dead node's own lane, whose stop is never acknowledged, so its
    ///     agents never cascade into a start anywhere. The wait is bounded only by
    ///     <see cref="AgentBatchTimeouts.ReplyWindowFor" /> — over twenty minutes for forty agents — and
    ///     throughout it the leader's ledger reports the move as outstanding, so the cluster runs silently
    ///     short with <c>assigned == running</c>. The node's registration is gone and its assignment rows went
    ///     with it, so there is nothing left to wait on.</para>
    /// </summary>
    public (Uri Agent, Guid Destination)[] AbandonLane(Guid nodeId)
    {
        // The shared lane belongs to no node. An Enqueue racing this can re-create the lane a moment after
        // the removal; that command pays its own reply window against a node that is gone and the next health
        // check abandons it again, so it is not worth a lock on the enqueue path.
        if (nodeId == SharedLane || !_lanes.TryRemove(nodeId, out var lane))
        {
            return [];
        }

        lane.Queue.Writer.TryComplete();

        // Before a single claim is let go, and not merely by cancelling at the end: the command in flight can
        // complete in the window between the release below and that cancellation, and a cascade escaping then
        // would be aimed at the departed node's destination while the leader, told the claim was released,
        // re-places the same agent elsewhere. Latched under the gate the cascade check takes, so a completing
        // command either cascades entirely before this or not at all.
        lane.MarkAbandoned();

        interleave(AbandonLatched);

        // _queued covers the command the lane is parked on as well as everything behind it: the parked one
        // holds the agents that matter. Only what release() clears is reported -- a command that just
        // finished, or whose claim a re-target has taken over, names agents belonging to a live dispatch.
        //
        // Matched on THIS lane and not merely on the destination, because _queued is global and the removal
        // above does not stop an Enqueue for the same node from landing on a replacement lane a moment
        // later. Such a command is live -- its worker is running it, and only the next sweep abandons it --
        // so releasing its claims here would report agents to the leader that are still being placed.
        var released = new List<(Uri Agent, Guid Destination)>();
        foreach (var pair in _queued.ToArray())
        {
            if (!ReferenceEquals(pair.Value.Lane, lane))
            {
                continue;
            }

            release(pair.Value, nodeId, released);
        }

        interleave(AbandonReleased);

        // No need to wait on the worker: it reads a token captured before this, and cancelling first means
        // the one use that would touch the disposed source -- registering a callback -- runs inline instead.
        // Waiting would never finish for the lane that needs this most, the one parked on an await that
        // ignores its token.
        lane.CancelAndDispose();

        return released.Distinct().ToArray();
    }

    // Consecutive sweeps each lane's node has been missing from the roster, in the manner of
    // ejectStaleNodes' _staleObservations. Only ever touched from the serialized health-check path.
    private readonly Dictionary<Guid, int> _absences = new();

    /// <summary>
    ///     Abandon the lane of every node that has been absent from <paramref name="registeredNodes" /> for
    ///     <paramref name="absenceThreshold" /> consecutive sweeps, and return the agents whose claims that
    ///     releases, each with the node it was claimed for.
    ///
    ///     <para>Driven by the roster rather than by the ejection itself. Ejecting a stale row is not the
    ///     leader's privilege — <c>ejectStaleNodes</c> spares only the <i>current leader's</i> row — so any
    ///     node may delete the corpse, and on a three-node cluster a follower commonly wins that race. The
    ///     wedged commands exist only on the leader's dispatcher, so keying the release off "the node I just
    ///     ejected" fires it on a node with nothing to release and leaves the leader's own lanes wedged.
    ///     A membership test gives the same answer whoever performs the delete.</para>
    /// </summary>
    public (Uri Agent, Guid Destination)[] AbandonLanesExcept(IReadOnlySet<Guid> registeredNodes,
        int absenceThreshold)
    {
        // Departed means gone from the store's snapshot, not merely stale: a node inside the ejection
        // hysteresis window still holds its assignment rows, so releasing its in-flight starts would race a
        // blip back to life and land two copies. An ejected node is absent from the very next snapshot.
        var departed = _lanes.Keys.Where(id => id != SharedLane && !registeredNodes.Contains(id)).ToHashSet();

        // A node back in the roster starts over, so only a sustained absence abandons anything.
        foreach (var returned in _absences.Keys.Where(id => !departed.Contains(id)).ToArray())
        {
            _absences.Remove(returned);
        }

        // Hysteresis, for the reason ejectStaleNodes has it and off the same setting: this cancels the
        // command running in the lane, and a snapshot that lags or is read mid-write can leave a live node
        // out for one tick. Tearing that node's start down mid-flight leaves whatever it had already begun
        // running, with nothing to stop it once the leader places those agents elsewhere.
        var threshold = Math.Max(1, absenceThreshold);

        List<(Uri Agent, Guid Destination)>? released = null;
        foreach (var nodeId in departed)
        {
            var count = (_absences.TryGetValue(nodeId, out var previous) ? previous : 0) + 1;
            _absences[nodeId] = count;

            if (count < threshold)
            {
                continue;
            }

            _absences.Remove(nodeId);

            var freed = AbandonLane(nodeId);
            if (freed.Length > 0)
            {
                (released ??= []).AddRange(freed);
            }
        }

        return released?.ToArray() ?? [];
    }

    private Lane laneFor(Guid destination)
    {
        // GetOrAdd's factory can run more than once under contention, so the worker is started through a
        // Lazy inside the Lane rather than by the factory itself. A Lane also owns a linked
        // CancellationTokenSource, whose registration on the runtime token outlives a discarded instance, so
        // the loser of that race has to be disposed rather than simply dropped.
        if (!_lanes.TryGetValue(destination, out var lane))
        {
            var candidate = new Lane(_cancellation);
            lane = _lanes.GetOrAdd(destination, candidate);

            if (!ReferenceEquals(lane, candidate))
            {
                candidate.CancelAndDispose();
            }
        }

        lane.Start(this, destination);
        return lane;
    }

    private async Task runLaneAsync(Lane lane, Guid destination)
    {
        while (!lane.Token.IsCancellationRequested && !_disposing)
        {
            Dispatch dispatch;
            try
            {
                dispatch = await lane.Queue.Reader.ReadAsync(lane.Token);
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
            //
            // Abandonment reads the same way and for the same reason, with one of its own: the latch goes
            // up before AbandonLane releases the claims, and the cancellation that would break this read
            // only afterwards. A command finishing in between would otherwise pick the next buffered
            // dispatch up and execute it -- a live stop or start against a node that has left the cluster,
            // for agents the leader has already been told are free.
            interleave(LaneRead);

            if (_disposing || lane.IsAbandoned)
            {
                release(dispatch, destination);
                return;
            }

            var command = dispatch.Command;

            try
            {
                var cascaded = await _executor(command, lane.Token);

                interleave(LaneExecuted);

                // Nothing cascades out of an abandoned lane. A command that finished normally in the window
                // after AbandonLane let its claims go -- or one that swallowed the cancellation -- would
                // otherwise enqueue a follow-up aimed at the destination the leader has since re-decided,
                // and Enqueue's dedup cannot catch it because the two destinations differ. TryCascade tests
                // and enqueues under AbandonLane's own gate, so the two cannot interleave.
                if (cascaded != null)
                {
                    // Route a cascade back through Enqueue rather than executing it here, so it lands in the
                    // lane of the node it actually targets -- e.g. ReassignAgent runs in the source node's
                    // lane (GH-3749) and cascades an AssignAgent that belongs in the destination's lane.
                    lane.TryCascade(() =>
                    {
                        foreach (var next in cascaded) Enqueue(next);
                    });
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (lane.IsAbandoned)
            {
                // The node departed mid-command. Nothing to retry: AbandonLane has let the claims go and
                // the leader re-places the agents on its next evaluation.
                _logger.LogInformation(
                    "Abandoned agent command {Command} against node {NodeId}, which has left the cluster",
                    command, destination);
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
                release(dispatch, destination);
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
            finally
            {
                // After the wait, never before: a lane that beat the budget is done, and one that did not
                // has just spent its whole grace period.
                pair.Value.CancelAndDispose();
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
        private readonly object _cascadeGate = new();
        private volatile bool _abandoned;
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationToken _token;

        public Lane(CancellationToken parent)
        {
            // Linked, so runtime shutdown still cancels every lane at once, while AbandonLane can cancel
            // exactly one: the command executing against a node that has left the cluster is waiting on an
            // acknowledgement that is never coming, and its reply window is measured in tens of minutes.
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);

            // Held as a field because CancellationTokenSource.Token throws once the source is disposed,
            // while a token already handed out goes on answering IsCancellationRequested. That is what lets
            // the worker survive a lane disposed out from under it.
            _token = _cancellation.Token;
        }

        public Channel<Dispatch> Queue { get; } =
            Channel.CreateUnbounded<Dispatch>(new UnboundedChannelOptions { SingleReader = true });

        public Task? Worker => _worker;

        public CancellationToken Token => _token;

        public bool IsAbandoned => _abandoned || _token.IsCancellationRequested;

        /// <summary>
        ///     Latch abandonment ahead of the cancellation, under the gate <see cref="TryCascade" /> takes.
        ///     Cancelling is what unwedges the command in flight, but it comes last in AbandonLane -- after
        ///     the claims are released -- and a command completing in between would otherwise still see a
        ///     live lane and cascade into it.
        /// </summary>
        public void MarkAbandoned()
        {
            lock (_cascadeGate)
            {
                _abandoned = true;
            }
        }

        /// <summary>
        ///     Run <paramref name="cascade" /> unless the lane has been abandoned, atomically against
        ///     <see cref="MarkAbandoned" />. Returns whether it ran. Nothing here blocks: the cascade is a
        ///     handful of channel writes.
        /// </summary>
        public bool TryCascade(Action cascade)
        {
            lock (_cascadeGate)
            {
                if (IsAbandoned)
                {
                    return false;
                }

                cascade();
                return true;
            }
        }

        private void cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down by DisposeAsync; there is nothing left to unwind.
            }
        }

        /// <summary>
        ///     Cancel, then dispose — in that order, always. A registration made against the token afterwards
        ///     would throw on a merely-disposed source, but runs inline and harmlessly on a cancelled one.
        /// </summary>
        public void CancelAndDispose()
        {
            cancel();
            _cancellation.Dispose();
        }

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
