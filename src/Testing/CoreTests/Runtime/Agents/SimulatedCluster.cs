using System.Diagnostics;
using CoreTests.Transports;
using JasperFx.Core;
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
internal sealed class SimulatedCluster
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

    // The persisted assignment rows per node, as a set. WolverineNode.ActiveAgents is the List<Uri> the
    // controller reads; it is rebuilt from this before every evaluation so that stops and lands cost O(1)
    // here instead of a List.Remove / Fill scan each — at sixty thousand agents the harness must not be
    // the quadratic thing being measured.
    private readonly Dictionary<Guid, HashSet<Uri>> _persistedByNode = new();
    private readonly HashSet<Guid> _dirtyNodes = [];

    private readonly List<int> _runningCountByRound = [];
    private readonly List<string> _doubleStartReports = [];
    private readonly List<string> _undeclaredPlacements = [];
    private readonly Dictionary<Uri, int> _dispatchCounts = new();
    private readonly List<TimeSpan> _evaluationTimes = [];

    private record struct InFlightStart(Guid NodeId, int RoundsRemaining);

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

    /// <summary>Wall-clock cost of each leader evaluation, in round order.</summary>
    public IReadOnlyList<TimeSpan> EvaluationTimes => _evaluationTimes;

    public IReadOnlyList<Uri> RunningAgents => _running.Keys.ToList();

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
        _persistedByNode.Remove(nodeId);
        _dirtyNodes.Remove(nodeId);

        foreach (var uri in _running.Where(x => x.Value == nodeId).Select(x => x.Key).ToArray()) _running.Remove(uri);
        foreach (var uri in _awaitingVisibility.Where(x => x.Value == nodeId).Select(x => x.Key).ToArray()) _awaitingVisibility.Remove(uri);
        foreach (var uri in _inFlight.Where(x => x.Value.NodeId == nodeId).Select(x => x.Key).ToArray()) _inFlight.Remove(uri);
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

        var newId = Options.UniqueNodeId;
        node.NodeId = newId;

        _persistedByNode[newId] = _persistedByNode[nodeId];
        _persistedByNode.Remove(nodeId);
        if (_dirtyNodes.Remove(nodeId)) _dirtyNodes.Add(newId);

        foreach (var uri in _running.Where(x => x.Value == nodeId).Select(x => x.Key).ToArray()) _running[uri] = newId;
        foreach (var uri in _awaitingVisibility.Where(x => x.Value == nodeId).Select(x => x.Key).ToArray()) _awaitingVisibility[uri] = newId;
        foreach (var uri in _inFlight.Where(x => x.Value.NodeId == nodeId).Select(x => x.Key).ToArray())
        {
            _inFlight[uri] = _inFlight[uri] with { NodeId = newId };
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

        var stopwatch = Stopwatch.StartNew();
        var commands = await _controller.EvaluateAssignmentsAsync(_nodes.ToList(), new AgentRestrictions());
        _evaluationTimes.Add(stopwatch.Elapsed);

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

        return commands;
    }

    /// <summary>Runs rounds until every known agent is running and the leader has nothing left to say.</summary>
    public async Task<int> RunUntilConvergedAsync(int maxRounds)
    {
        for (var round = 1; round <= maxRounds; round++)
        {
            var commands = await RunRoundAsync();

            if (commands.Count == 0 && _inFlight.Count == 0 && _awaitingVisibility.Count == 0
                && _running.Count == AllAgents.Length)
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
            NullLogger<NodeAgentController>.Instance, CancellationToken.None);

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

            // Running now — but invisible to the leader until the promotion above runs next round.
            _running[uri] = pending.NodeId;
            _awaitingVisibility[uri] = pending.NodeId;

            // What AssignAgent/AssignAgents do the moment a start confirms, and the reason the agent stays
            // held for the one snapshot cycle it takes the persisted assignment row to become visible.
            _controller.ConfirmDispatched([uri], pending.NodeId);
        }
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

        if (_running.TryGetValue(agentUri, out var runningOn) && runningOn == nodeId)
        {
            _running.Remove(agentUri);
        }

        if (_awaitingVisibility.TryGetValue(agentUri, out var pendingVisible) && pendingVisible == nodeId)
        {
            _awaitingVisibility.Remove(agentUri);
        }

        if (_inFlight.TryGetValue(agentUri, out var pending) && pending.NodeId == nodeId)
        {
            _inFlight.Remove(agentUri);
        }
    }
}
