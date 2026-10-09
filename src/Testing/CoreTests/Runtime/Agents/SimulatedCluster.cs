using System.Diagnostics;
using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// A simulated multi-node cluster driving the leader's real <see cref="NodeAgentController.EvaluateAssignmentsAsync" />.
/// One <see cref="RunRoundAsync" /> is one health-check tick: in-flight starts advance, the leader evaluates, and
/// the commands it emits are applied to the cluster's state the way the corresponding agent commands would
/// apply them for real.
///
/// <para>Started life as the private harness behind <see cref="slow_agent_start_convergence" /> (GH-3779). Lifted
/// out and given a lifecycle — nodes joining, nodes leaving, the leader being replaced — because the field
/// reports behind GH-4886 are not about one slow wave against a stable node set: they are about a fleet that
/// deploys a new version every few days, scales up for the warm-up, scales back down, and loses its leader
/// mid-wave. Every one of those transitions runs the same evaluation, and the assignment invariants have to
/// hold across all of them, not just at the end of a single wave.</para>
///
/// <para><b>Time is measured in evaluation rounds, not milliseconds.</b> Everything here is seeded and
/// deterministic — a failure reproduces. The one wall-clock reading taken is <see cref="EvaluationTimes" />,
/// the cost of each leader evaluation, which is what GH-4886 was about.</para>
/// </summary>
internal sealed class SimulatedCluster : IAsyncDisposable
{
    private IWolverineRuntime _runtime;
    private FakeAgentFamily _family;
    private NodeAgentController _controller;
    private readonly List<WolverineNode> _nodes = [];
    private readonly Dictionary<Uri, InFlightStart> _inFlight = new();

    // Ground truth: where each agent is actually running. Deliberately NOT the same thing as the leader's
    // view of it — a node persists its assignment row only after the agent is up, and the leader reads
    // that row on a later snapshot, so there is a window in which an agent is genuinely running and looks
    // completely unplaced. That window is what GH-3750 is about, and a simulation that closes it
    // instantly cannot reproduce the field's falling assigned-agent count.
    private readonly Dictionary<Uri, Guid> _running = new();
    private readonly Dictionary<Uri, Guid> _awaitingVisibility = new();

    // GH-4894: further copies of an agent that came up on a second node while _running already had it
    // elsewhere. A leader that dies with starts in flight is replaced by one with an empty ledger, which
    // re-decides those agents and can send one to a different node: two live copies until the next
    // evaluation sees both rows and stops the older one (GH-2602). The soak asserts that healing happens,
    // which needs the duplicate to be modelled rather than overwritten.
    private readonly Dictionary<Uri, HashSet<Guid>> _extraCopies = new();

    // The persisted assignment rows per node, as a set. WolverineNode.ActiveAgents is the List<Uri> the
    // controller reads; it is rebuilt from this before every evaluation so that stops and lands cost O(1)
    // here instead of a List.Remove / Fill scan each — at sixty thousand agents the harness must not be
    // the quadratic thing being measured.
    private readonly Dictionary<Guid, HashSet<Uri>> _persistedByNode = new();
    private readonly HashSet<Guid> _dirtyNodes = [];
    private readonly HashSet<Uri> _orphanedLastTick = [];

    private readonly List<int> _runningCountByRound = [];
    private readonly List<string> _doubleStartReports = [];
    private readonly List<string> _undeclaredPlacements = [];
    private readonly Dictionary<Uri, int> _dispatchCounts = new();
    private readonly List<TimeSpan> _evaluationTimes = [];
    private readonly ErrorLog _leaderLog = new();

    private record struct InFlightStart(Guid NodeId, int RoundsRemaining);

    // Dispatcher lanes, opt-in through UseDispatcherLanes. Every node that has led owns a real
    // AgentCommandDispatcher, as its runtime does, and keeps it after losing the leadership.
    private readonly Dictionary<Guid, (AgentCommandDispatcher Dispatcher, CancellationTokenSource Cancellation)>
        _dispatchers = new();

    // Dispatchers of nodes that have left, cancelled and awaiting disposal.
    private readonly List<(AgentCommandDispatcher Dispatcher, CancellationTokenSource Cancellation)> _retiredDispatchers = [];

    // Commands the lanes handed to the executor that the simulation has not taken up yet, and every command
    // still parked in the executor. Written from the lane threads, hence the lock.
    private readonly object _laneGate = new();
    private readonly List<LaneWork> _laneWork = [];
    private readonly HashSet<IAgentCommand> _parked = new(ReferenceEqualityComparer.Instance);

    // A start command being worked through on its destination: MaxAgentStartParallelism agents at a time,
    // replying once the whole batch is done, as StartAgents does.
    private readonly Dictionary<Guid, List<NodeBatch>> _batches = new();
    private readonly Dictionary<Uri, NodeBatch> _startingIn = new();

    private sealed record LaneWork(IAgentCommand Command, TaskCompletionSource<AgentCommands?> Completion);

    private sealed class NodeBatch(LaneWork work, IEnumerable<Uri> agents)
    {
        public LaneWork Work { get; } = work;
        public Queue<Uri> Waiting { get; } = new(agents);
        public HashSet<Uri> Starting { get; } = [];
        public bool IsDone => Waiting.Count == 0 && Starting.Count == 0;
    }

    public SimulatedCluster(int nodeCount, int agentCount, int seed)
        : this(nodeCount, new FakeAgentFamily("fake", agentCount), seed)
    {
    }

    /// <summary>
    ///     The seams a blue/green scenario needs: the leader's own <paramref name="family" /> (which, as in
    ///     production, may enumerate only its OWN fleet's agents), and per-node capability sets via
    ///     <paramref name="capabilitiesFor" /> (node index -> declared agents). The other fleet's agents
    ///     reach the leader's grid exactly the way they do for real — through the capability union
    ///     (NodeAgentController.EvaluateAssignmentsAsync seeds the grid from every node's capabilities).
    /// </summary>
    public SimulatedCluster(int nodeCount, FakeAgentFamily family, int seed,
        Func<int, Uri[]>? capabilitiesFor = null)
    {
        Seed = seed;
        _family = family;

        Options = buildOptions();
        (_runtime, _controller) = buildLeader(Options, family);

        var familyAgents = family.AllAgentUris();

        // Node 0 is this process — the controller injects self into any node list that omits it, so the
        // leader has to BE one of the simulated nodes rather than a sixth observer.
        for (var i = 0; i < nodeCount; i++)
        {
            addNode(i == 0 ? Options.UniqueNodeId : Guid.NewGuid(), capabilitiesFor?.Invoke(i) ?? familyAgents);
        }

        StartCost = _ => 1;
    }

    public WolverineOptions Options { get; private set; }
    public int Seed { get; }

    /// <summary>
    ///     Every agent the leader can currently know about: the union of the live nodes' capabilities and the
    ///     leader's own family enumeration — exactly how <c>EvaluateAssignmentsAsync</c> seeds its grid. This
    ///     changes as nodes come and go, which is the point.
    /// </summary>
    public Uri[] AllAgents => _nodes.SelectMany(x => x.Capabilities).Concat(_family.AllAgentUris()).Distinct().ToArray();

    /// <summary>How many rounds each agent's start takes to complete on its destination node.</summary>
    public Func<Uri, int> StartCost { get; set; }

    public IReadOnlyList<int> RunningCountByRound => _runningCountByRound;
    public IReadOnlyList<string> DoubleStartReports => _doubleStartReports;

    /// <summary>
    ///     GH-4555's failure mode, caught at the moment of dispatch: a start sent to a node that does not declare
    ///     the agent while some other live node does. The target cannot build it, so it never starts and the
    ///     leader re-decides the identical placement every round.
    /// </summary>
    public IReadOnlyList<string> UndeclaredPlacements => _undeclaredPlacements;

    public IReadOnlyDictionary<Uri, int> DispatchCounts => _dispatchCounts;
    public int StopsEmitted { get; private set; }
    public int ReassignmentsEmitted { get; private set; }

    /// <summary>
    ///     Errors the leader logged. A family whose distribution throws is caught and logged by the evaluation,
    ///     which then carries on with whatever the grid held -- easy to mistake for a placement decision.
    /// </summary>
    public IReadOnlyList<string> LeaderErrors => _leaderLog.Errors;

    /// <summary>Called at the end of every round, for invariants a scenario checks as it goes.</summary>
    public Action<AgentCommands>? AfterRound { get; set; }

    /// <summary>Wall-clock cost of each leader evaluation, in round order.</summary>
    public IReadOnlyList<TimeSpan> EvaluationTimes => _evaluationTimes;

    /// <summary>Starts dispatched but not yet visible to the leader as persisted assignment rows.</summary>
    public int InFlightStarts => _inFlight.Count + _awaitingVisibility.Count;

    /// <summary>
    ///     Whether commands go through real <see cref="AgentCommandDispatcher" /> lanes rather than being applied
    ///     the moment the leader emits them. See <see cref="UseDispatcherLanes" />.
    /// </summary>
    public bool UsesDispatcherLanes { get; private set; }

    /// <summary>The leader of record: the node whose controller evaluates.</summary>
    public Guid LeaderNodeId => Options.UniqueNodeId;

    public IReadOnlyList<Uri> RunningAgents => _running.Keys.ToList();

    /// <summary>Agents currently running on more than one node: copies beyond the first, summed.</summary>
    public int DuplicateCopies => _extraCopies.Values.Sum(x => x.Count);

    /// <summary>Each live node's id and the agents it declares.</summary>
    public IEnumerable<(Guid NodeId, IReadOnlyList<Uri> Capabilities)> Declarations
        => _nodes.Select(x => (x.NodeId, (IReadOnlyList<Uri>)x.Capabilities));

    /// <summary>Ground truth of where each agent is actually running, for placement assertions.</summary>
    public IReadOnlyDictionary<Uri, Guid> RunningAssignments => _running;

    public IReadOnlyList<Guid> NodeIds => _nodes.Select(x => x.NodeId).ToList();

    public Guid NodeIdAt(int index) => _nodes[index].NodeId;

    public int[] RunningCountsByNode
        => _nodes.Select(node => _running.Count(x => x.Value == node.NodeId)).ToArray();

    public int RunningCountOn(Guid nodeId) => _running.Count(x => x.Value == nodeId);

    public IEnumerable<Uri> AssignedIn(AgentCommands commands)
        => commands.OfType<AssignAgents>().SelectMany(x => x.AgentIds)
            .Concat(commands.OfType<AssignAgent>().Select(x => x.AgentUri));

    /// <summary>
    ///     The load a node advertises on its heartbeat (see <see cref="WolverineNode.LoadFactor" />); null for
    ///     none. Only read by the leader when <see cref="DurabilitySettings.CapacityAwareAssignment" /> is on.
    /// </summary>
    public void AdvertiseLoad(Guid nodeId, double? loadFactor)
        => _nodes.Single(x => x.NodeId == nodeId).LoadFactor = loadFactor;

    /// <summary>
    ///     Route the leader's commands through a real <see cref="AgentCommandDispatcher" /> instead of applying
    ///     them on the spot: a serial lane per destination node, a reassignment run in its SOURCE node's lane,
    ///     and the dispatcher as the leader's pending-dispatch probe, as in production. A start command is then
    ///     worked through on its node <see cref="DurabilitySettings.MaxAgentStartParallelism" /> agents at a
    ///     time, each taking <see cref="StartCost" /> rounds, and its lane moves on once the whole batch is done.
    ///
    ///     <para>Without this every start runs concurrently with every other, so a node never builds up a
    ///     backlog and nothing queued behind one can be held up -- which is the shape of GH-4901.</para>
    /// </summary>
    public void UseDispatcherLanes()
    {
        UsesDispatcherLanes = true;
        attachDispatcher();
    }

    private void attachDispatcher()
    {
        var leaderId = Options.UniqueNodeId;
        if (!_dispatchers.TryGetValue(leaderId, out var entry))
        {
            var cancellation = new CancellationTokenSource();
            var dispatcher = new AgentCommandDispatcher(parkForSimulation, NullLogger.Instance, cancellation.Token);
            entry = (dispatcher, cancellation);
            _dispatchers[leaderId] = entry;
        }

        _controller.PendingDispatches = entry.Dispatcher.TryFindPendingDestination;
    }

    // The dispatcher's executor. Nothing happens here: the command is parked until the simulation applies it
    // between rounds, so the lanes move deterministically.
    private Task<AgentCommands?> parkForSimulation(IAgentCommand command, CancellationToken token)
    {
        var completion = new TaskCompletionSource<AgentCommands?>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => completion.TrySetCanceled(token));

        lock (_laneGate)
        {
            _laneWork.Add(new LaneWork(command, completion));
            _parked.Add(command);
        }

        return completion.Task;
    }

    /// <summary>
    ///     A node joins the cluster declaring <paramref name="capabilities" /> and running nothing — a scale-up,
    ///     or the first node of a new version coming up for its warm-up.
    /// </summary>
    public Guid AddNode(Uri[] capabilities)
    {
        var id = Guid.NewGuid();
        addNode(id, capabilities);
        return id;
    }

    /// <summary>
    ///     A node leaves — a scale-down, a crash, or the old version being torn down after a blue/green cutover.
    ///     Everything it was running stops with it, and any start still in flight to it is lost. The leader's
    ///     node snapshot simply stops listing it, which is all a departed node ever is to the leader.
    /// </summary>
    public void RemoveNode(Guid nodeId)
    {
        if (nodeId == Options.UniqueNodeId)
        {
            throw new InvalidOperationException("Fail the leader over to another node before removing it");
        }

        _nodes.RemoveAll(x => x.NodeId == nodeId);

        // For one tick the departed node is what the leader sees as a STALE row -- still listing the agents
        // it was running, filtered out of the grid -- before it is ejected and the row is gone (GH-4897).
        if (_persistedByNode.Remove(nodeId, out var itsAgents))
        {
            foreach (var uri in itsAgents) _orphanedLastTick.Add(uri);
        }

        _dirtyNodes.Remove(nodeId);

        foreach (var uri in _running.Where(x => x.Value == nodeId).Select(x => x.Key).ToArray()) forgetCopy(uri, nodeId);
        foreach (var (uri, copies) in _extraCopies.ToArray())
        {
            copies.Remove(nodeId);
            if (copies.Count == 0) _extraCopies.Remove(uri);
        }

        foreach (var uri in _awaitingVisibility.Where(x => x.Value == nodeId).Select(x => x.Key).ToArray()) _awaitingVisibility.Remove(uri);
        foreach (var uri in _inFlight.Where(x => x.Value.NodeId == nodeId).Select(x => x.Key).ToArray()) _inFlight.Remove(uri);

        // Work sent to the departed node fails. Its own dispatcher, if it ever led, goes with it: what it still
        // had queued is never sent, while what it already sent carries on at the destination.
        failBatchesOn(nodeId, $"Node {nodeId} has left the cluster");

        if (_dispatchers.Remove(nodeId, out var departed))
        {
            departed.Cancellation.Cancel();
            _retiredDispatchers.Add(departed);
        }
    }

    private void failBatchesOn(Guid nodeId, string reason)
    {
        if (!_batches.Remove(nodeId, out var batches)) return;

        foreach (var batch in batches)
        {
            foreach (var uri in batch.Starting) _startingIn.Remove(uri);
            fail(batch.Work, new InvalidOperationException(reason));
        }
    }

    // The primary copy on nodeId is gone; a surviving extra copy, if any, becomes the primary.
    private void forgetCopy(Uri uri, Guid nodeId)
    {
        if (!_running.TryGetValue(uri, out var primary) || primary != nodeId) return;

        _running.Remove(uri);

        if (_extraCopies.TryGetValue(uri, out var copies) && copies.Count > 0)
        {
            var promoted = copies.First();
            copies.Remove(promoted);
            if (copies.Count == 0) _extraCopies.Remove(uri);
            _running[uri] = promoted;
        }
    }

    /// <summary>
    ///     The leader is replaced: a fresh <see cref="NodeAgentController" /> — empty pending-assignment ledger,
    ///     no memory of anything the previous leader dispatched — takes over on the node <paramref name="nodeId" />.
    ///     The new leader's family is <paramref name="family" /> when given, since in a blue/green fleet the new
    ///     leader's own store may enumerate a different version than the old one's did.
    ///
    ///     <para>A node's identity is <see cref="WolverineOptions.UniqueNodeId" />, which is read-only, so the
    ///     simulation gives the elected node the new options' id and carries its running agents across. The
    ///     agents never move; only the label on the node does.</para>
    /// </summary>
    public Guid FailOverLeaderTo(Guid nodeId, FakeAgentFamily? family = null)
    {
        var node = _nodes.Single(x => x.NodeId == nodeId);

        Options = buildOptions();
        _family = family ?? _family;
        (_runtime, _controller) = buildLeader(Options, _family);

        // What tryStartLeadershipAsync does on election: the takeover hold (GH-4897) counts from here.
        _controller.EvaluationsSinceElection = 0;

        var newId = Options.UniqueNodeId;
        node.NodeId = newId;

        _persistedByNode[newId] = _persistedByNode[nodeId];
        _persistedByNode.Remove(nodeId);
        if (_dirtyNodes.Remove(nodeId)) _dirtyNodes.Add(newId);

        foreach (var uri in _running.Where(x => x.Value == nodeId).Select(x => x.Key).ToArray()) _running[uri] = newId;
        foreach (var copies in _extraCopies.Values)
        {
            if (copies.Remove(nodeId)) copies.Add(newId);
        }

        foreach (var uri in _awaitingVisibility.Where(x => x.Value == nodeId).Select(x => x.Key).ToArray()) _awaitingVisibility[uri] = newId;
        foreach (var uri in _inFlight.Where(x => x.Value.NodeId == nodeId).Select(x => x.Key).ToArray())
        {
            _inFlight[uri] = _inFlight[uri] with { NodeId = newId };
        }

        if (UsesDispatcherLanes)
        {
            // Commands already addressed to the old label cannot follow the relabel, so they fail and the new
            // leader drives them again -- the same as a batch sent to a node that was replaced.
            if (_dispatchers.Remove(nodeId, out var own)) _dispatchers[newId] = own;
            failBatchesOn(nodeId, $"Node {nodeId} was relabelled");
            attachDispatcher();
        }

        return newId;
    }

    /// <summary>
    /// One health-check tick: land any starts whose cost has run out, evaluate, and apply the result.
    /// </summary>
    public async Task<AgentCommands> RunRoundAsync()
    {
        landCompletedStarts();
        syncPersistedAssignments();

        var declaredBy = _nodes.ToDictionary(x => x.NodeId, x => x.Capabilities.ToHashSet());

        // What the heartbeat path hands the evaluation for the stale nodes it filtered out this tick
        _controller.OrphanedByStaleNodes = _orphanedLastTick.ToHashSet();
        _orphanedLastTick.Clear();

        var stopwatch = Stopwatch.StartNew();
        var commands = await _controller.EvaluateAssignmentsAsync(_nodes.ToList(), new AgentRestrictions());
        _evaluationTimes.Add(stopwatch.Elapsed);

        if (UsesDispatcherLanes)
        {
            var dispatcher = _dispatchers[Options.UniqueNodeId].Dispatcher;
            foreach (var command in commands)
            {
                count(command);
                dispatcher.Enqueue(command);
            }

            await driveLanesAsync(declaredBy);
            _runningCountByRound.Add(_running.Count);
            AfterRound?.Invoke(commands);
            return commands;
        }

        foreach (var command in commands)
        {
            switch (command)
            {
                case AssignAgent assign:
                    dispatchStart(assign.AgentUri, assign.Destination.NodeId, declaredBy);
                    break;

                case AssignAgents assigns:
                    foreach (var uri in assigns.AgentIds) dispatchStart(uri, assigns.Destination.NodeId, declaredBy);
                    break;

                case ReassignAgent reassign:
                    ReassignmentsEmitted++;
                    stop(reassign.AgentUri, reassign.OriginalNode.NodeId);
                    dispatchStart(reassign.AgentUri, reassign.ActiveNode.NodeId, declaredBy);
                    break;

                case ReassignAgents reassigns:
                    ReassignmentsEmitted += reassigns.AgentUris.Length;
                    foreach (var uri in reassigns.AgentUris)
                    {
                        stop(uri, reassigns.OriginalNode.NodeId);
                        dispatchStart(uri, reassigns.ActiveNode.NodeId, declaredBy);
                    }

                    break;

                case StopRemoteAgent stopOne:
                    StopsEmitted++;
                    stop(stopOne.AgentUri, stopOne.Destination.NodeId);
                    break;

                case StopRemoteAgents stopMany:
                    StopsEmitted += stopMany.AgentIds.Length;
                    foreach (var uri in stopMany.AgentIds) stop(uri, stopMany.Destination.NodeId);
                    break;
            }
        }

        _runningCountByRound.Add(_running.Count);
        AfterRound?.Invoke(commands);

        return commands;
    }

    private void count(IAgentCommand command)
    {
        switch (command)
        {
            case ReassignAgent:
                ReassignmentsEmitted++;
                break;
            case ReassignAgents reassigns:
                ReassignmentsEmitted += reassigns.AgentUris.Length;
                break;
            case StopRemoteAgent:
                StopsEmitted++;
                break;
            case StopRemoteAgents stops:
                StopsEmitted += stops.AgentIds.Length;
                break;
        }
    }

    // Let the lanes run until each is idle or parked on a start its node is still working through. A reassignment
    // or a stop completes at once and may cascade into another lane, so this goes round until nothing new turns
    // up, and then each node begins as many of its waiting starts as its parallelism allows.
    private async Task driveLanesAsync(Dictionary<Guid, HashSet<Uri>> declaredBy)
    {
        while (true)
        {
            await waitForLanesAsync();

            List<LaneWork> work;
            lock (_laneGate)
            {
                work = _laneWork.Where(x => !x.Completion.Task.IsCompleted).ToList();
                _laneWork.Clear();
            }

            if (work.Count == 0) break;

            foreach (var item in work.OrderBy(x => nodeNumberOf(x.Command.DestinationNodeId))
                         .ThenBy(x => x.Command.GetType().Name, StringComparer.Ordinal)
                         .ThenBy(x => firstAgentOf(x.Command), StringComparer.Ordinal))
            {
                apply(item);
            }
        }

        var parallelism = Math.Max(1, Options.Durability.MaxAgentStartParallelism);
        foreach (var node in _nodes)
        {
            if (!_batches.TryGetValue(node.NodeId, out var batches)) continue;

            foreach (var batch in batches)
            {
                while (batch.Starting.Count < parallelism && batch.Waiting.Count > 0)
                {
                    var uri = batch.Waiting.Dequeue();
                    dispatchStart(uri, node.NodeId, declaredBy);
                    batch.Starting.Add(uri);
                    _startingIn[uri] = batch;
                }
            }
        }
    }

    private void apply(LaneWork item)
    {
        switch (item.Command)
        {
            case AssignAgent assign:
                startBatch(item, assign.Destination.NodeId, [assign.AgentUri]);
                break;

            case AssignAgents assigns:
                startBatch(item, assigns.Destination.NodeId, assigns.AgentIds);
                break;

            case ReassignAgent reassign:
                complete(item, move(reassign.OriginalNode, reassign.ActiveNode, [reassign.AgentUri]));
                break;

            case ReassignAgents reassigns:
                complete(item, move(reassigns.OriginalNode, reassigns.ActiveNode, reassigns.AgentUris));
                break;

            case StopRemoteAgent stopOne:
                stop(stopOne.AgentUri, stopOne.Destination.NodeId);
                complete(item, AgentCommands.Empty);
                break;

            case StopRemoteAgents stopMany:
                foreach (var uri in stopMany.AgentIds) stop(uri, stopMany.Destination.NodeId);
                complete(item, AgentCommands.Empty);
                break;

            default:
                complete(item, AgentCommands.Empty);
                break;
        }
    }

    private void startBatch(LaneWork item, Guid nodeId, Uri[] agents)
    {
        if (_nodes.All(x => x.NodeId != nodeId))
        {
            fail(item, new InvalidOperationException($"Node {nodeId} is not in the cluster"));
            return;
        }

        if (!_batches.TryGetValue(nodeId, out var batches)) _batches[nodeId] = batches = [];
        batches.Add(new NodeBatch(item, agents));
    }

    // ReassignAgents: stop at the source, and only what was confirmed gone cascades as a start in the
    // destination's own lane.
    private AgentCommands move(NodeDestination source, NodeDestination destination, Uri[] agents)
    {
        if (_nodes.All(x => x.NodeId != source.NodeId)) return AgentCommands.Empty;

        foreach (var uri in agents) stop(uri, source.NodeId);

        return agents.Length == 1
            ? [new AssignAgent(agents[0], destination)]
            : [new AssignAgents(destination, agents)];
    }

    // A batch replies once every agent in it has come up (or been stopped while starting).
    private void completeFinishedBatches()
    {
        foreach (var batches in _batches.Values)
        {
            foreach (var batch in batches.Where(x => x.IsDone).ToArray())
            {
                batches.Remove(batch);
                complete(batch.Work, AgentCommands.Empty);
            }
        }
    }

    private void complete(LaneWork item, AgentCommands result)
    {
        lock (_laneGate) _parked.Remove(item.Command);
        item.Completion.TrySetResult(result);
    }

    private void fail(LaneWork item, Exception failure)
    {
        lock (_laneGate) _parked.Remove(item.Command);
        item.Completion.TrySetException(failure);
    }

    private async Task waitForLanesAsync()
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            lock (_laneGate)
            {
                var settled = _dispatchers.Values.All(entry => entry.Dispatcher.LaneStates.All(lane =>
                    lane.Executing == null ? lane.Queued == 0 : _parked.Contains(lane.Executing)));

                if (settled) return;
            }

            if (waited.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("The dispatcher lanes did not settle");
            }

            await Task.Yield();
        }
    }

    private int nodeNumberOf(Guid? nodeId)
        => _nodes.FirstOrDefault(x => x.NodeId == nodeId)?.AssignedNodeNumber ?? int.MaxValue;

    private static string firstAgentOf(IAgentCommand command) => command switch
    {
        AssignAgent x => x.AgentUri.ToString(),
        AssignAgents x => x.AgentIds.FirstOrDefault()?.ToString() ?? "",
        ReassignAgent x => x.AgentUri.ToString(),
        ReassignAgents x => x.AgentUris.FirstOrDefault()?.ToString() ?? "",
        StopRemoteAgent x => x.AgentUri.ToString(),
        StopRemoteAgents x => x.AgentIds.FirstOrDefault()?.ToString() ?? "",
        _ => ""
    };

    private bool lanesAreIdle()
    {
        if (!UsesDispatcherLanes) return true;
        if (_batches.Values.Any(x => x.Count > 0)) return false;

        return _dispatchers.Values.All(entry =>
            entry.Dispatcher.LaneStates.All(x => x.Executing == null && x.Queued == 0));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (dispatcher, cancellation) in _dispatchers.Values.Concat(_retiredDispatchers))
        {
            await cancellation.CancelAsync();
            await dispatcher.DisposeAsync();
            cancellation.Dispose();
        }

        _dispatchers.Clear();
        _retiredDispatchers.Clear();
    }

    /// <summary>Runs rounds until every known agent is running and the leader has nothing left to say.</summary>
    public async Task<int> RunUntilConvergedAsync(int maxRounds)
    {
        for (var round = 1; round <= maxRounds; round++)
        {
            var commands = await RunRoundAsync();

            if (commands.Count == 0 && _inFlight.Count == 0 && _awaitingVisibility.Count == 0
                && _running.Count == AllAgents.Length && lanesAreIdle())
            {
                return round;
            }
        }

        return maxRounds;
    }

    private static WolverineOptions buildOptions()
    {
        var options = new WolverineOptions { ApplicationAssembly = typeof(SimulatedCluster).Assembly };
        options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        options.Durability.DurabilityAgentEnabled = false;
        return options;
    }

    private (IWolverineRuntime, NodeAgentController) buildLeader(WolverineOptions options, FakeAgentFamily family)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        var controller = new NodeAgentController(runtime, Substitute.For<INodeAgentPersistence>(), [family],
            _leaderLog, CancellationToken.None);

        // The dispatcher holds a command from the moment it is queued until its lane is done with it,
        // whatever the outcome. An in-flight start here is exactly that hold.
        controller.PendingDispatches = (Uri agentUri, out Guid nodeId) =>
        {
            if (_inFlight.TryGetValue(agentUri, out var pending))
            {
                nodeId = pending.NodeId;
                return true;
            }

            nodeId = Guid.Empty;
            return false;
        };

        // What tryStartLeadershipAsync does on election; on a fresh cluster the takeover hold then sees no
        // peer holding assignments and stands down (GH-4897).
        controller.EvaluationsSinceElection = 0;

        return (runtime, controller);
    }

    private void addNode(Guid id, Uri[] capabilities)
    {
        var number = _nodes.Count == 0 ? 1 : _nodes.Max(x => x.AssignedNodeNumber) + 1;
        var node = new WolverineNode
        {
            NodeId = id,
            AssignedNodeNumber = number,
            ControlUri = new Uri($"fake://node{number}")
        };

        node.Capabilities.AddRange(capabilities);
        _nodes.Add(node);
        _persistedByNode[id] = [];
    }

    private void syncPersistedAssignments()
    {
        foreach (var nodeId in _dirtyNodes)
        {
            var node = _nodes.FirstOrDefault(x => x.NodeId == nodeId);
            if (node != null) node.ActiveAgents = _persistedByNode[nodeId].ToList();
        }

        _dirtyNodes.Clear();
    }

    private void landCompletedStarts()
    {
        // Agents that came up last round: their assignment row is now visible to the leader's snapshot.
        foreach (var (uri, nodeId) in _awaitingVisibility)
        {
            if (_persistedByNode.TryGetValue(nodeId, out var persisted) && persisted.Add(uri))
            {
                _dirtyNodes.Add(nodeId);
            }
        }

        _awaitingVisibility.Clear();

        foreach (var uri in _inFlight.Keys.ToArray())
        {
            var pending = _inFlight[uri];
            if (pending.RoundsRemaining > 1)
            {
                _inFlight[uri] = pending with { RoundsRemaining = pending.RoundsRemaining - 1 };
                continue;
            }

            _inFlight.Remove(uri);
            if (_startingIn.Remove(uri, out var batch)) batch.Starting.Remove(uri);

            // Running now — but invisible to the leader until the promotion above runs next round. A copy
            // coming up while another node already runs the agent is a duplicate, kept as one.
            if (_running.TryGetValue(uri, out var elsewhere) && elsewhere != pending.NodeId)
            {
                if (!_extraCopies.TryGetValue(uri, out var copies)) _extraCopies[uri] = copies = [];
                copies.Add(pending.NodeId);
            }
            else
            {
                _running[uri] = pending.NodeId;
            }

            _awaitingVisibility[uri] = pending.NodeId;

            // What AssignAgent/AssignAgents do the moment a start confirms, and the reason the agent stays
            // held for the one snapshot cycle it takes the persisted assignment row to become visible.
            _controller.ConfirmDispatched([uri], pending.NodeId);
        }

        completeFinishedBatches();
    }

    private void dispatchStart(Uri agentUri, Guid nodeId, Dictionary<Guid, HashSet<Uri>> declaredBy)
    {
        _dispatchCounts.TryGetValue(agentUri, out var count);
        _dispatchCounts[agentUri] = count + 1;

        // The single-copy invariant, asserted at the moment it would be violated rather than inferred from
        // the end state — a second copy that is later stopped still ran twice.
        if (_inFlight.TryGetValue(agentUri, out var already) && already.NodeId != nodeId)
        {
            _doubleStartReports.Add(
                $"{agentUri} dispatched to node {nodeId} while a start was still in flight to node {already.NodeId}");
        }

        if (_running.TryGetValue(agentUri, out var runningOn) && runningOn != nodeId)
        {
            _doubleStartReports.Add(
                $"{agentUri} dispatched to node {nodeId} while already running on node {runningOn}");
        }

        // The capability invariant: only when SOME node declares the agent, mirroring
        // NodeAgentController.WarnAboutUndeclaredAssignments — an agent no node declares is the GH-3341
        // stale-snapshot case and is deliberately placed anywhere.
        if (declaredBy.TryGetValue(nodeId, out var declared) && !declared.Contains(agentUri)
            && declaredBy.Values.Any(x => x.Contains(agentUri)))
        {
            _undeclaredPlacements.Add($"{agentUri} dispatched to node {nodeId}, which does not declare it");
        }

        _inFlight[agentUri] = new InFlightStart(nodeId, Math.Max(1, StartCost(agentUri)));
    }

    private void stop(Uri agentUri, Guid nodeId)
    {
        if (_persistedByNode.TryGetValue(nodeId, out var persisted) && persisted.Remove(agentUri))
        {
            _dirtyNodes.Add(nodeId);
        }

        forgetCopy(agentUri, nodeId);

        if (_extraCopies.TryGetValue(agentUri, out var copies) && copies.Remove(nodeId) && copies.Count == 0)
        {
            _extraCopies.Remove(agentUri);
        }

        if (_awaitingVisibility.TryGetValue(agentUri, out var pendingVisible) && pendingVisible == nodeId)
        {
            _awaitingVisibility.Remove(agentUri);
        }

        if (_inFlight.TryGetValue(agentUri, out var pending) && pending.NodeId == nodeId)
        {
            _inFlight.Remove(agentUri);
            if (_startingIn.Remove(agentUri, out var batch)) batch.Starting.Remove(agentUri);
        }
    }

    private sealed class ErrorLog : ILogger<NodeAgentController>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors.Add($"{formatter(state, exception)} {exception}");
        }
    }
}
