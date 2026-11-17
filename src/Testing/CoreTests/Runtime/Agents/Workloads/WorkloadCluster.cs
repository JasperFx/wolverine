using System.Text.RegularExpressions;
using Bobcat;
using Bobcat.Engine;
using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Recurring;

namespace CoreTests.Runtime.Agents.Workloads;

/// <summary>
///     GH-5601. The vocabulary of the node workload specifications: a cluster of nodes that differ only in
///     <see cref="DurabilitySettings.Workloads" />, an application with agents of every family, and the leader's real
///     <see cref="NodeAgentController.EvaluateAssignmentsAsync" /> deciding where each agent runs.
/// </summary>
/// <remarks>
///     <para>
///         What is real: the leader's evaluation, every distribution path of <see cref="AssignmentGrid" />, the
///         capabilities and markers each node advertises (<see cref="NodeAgentController.BuildCapabilities" />, the
///         code a node runs at startup), <see cref="SingularAgent" />, and the durability family's affinity and
///         warning. What is simulated: the agent families are stand-ins that distribute exactly the way the real
///         families do -- one line each -- because the real ones need endpoints, transports and event stores, and
///         an agent starts on the node it was assigned to as soon as the leader says so.
///     </para>
///     <para>
///         Nodes are written the way a person describes a deployment: <c>1 (HTTP only)</c>,
///         <c>2 (Messaging, Projections)</c>, <c>3 (All)</c>, <c>4 (6.x)</c> for a node from before workloads that
///         publishes no marker, and <c>5 (warm-up)</c> for a blue/green warm-up node.
///     </para>
/// </remarks>
public sealed class WorkloadCluster
{
    public const string Durability = "wolverinedb://default";
    public const string Projection = "event-subscriptions://marten/main/main/trips/all/v1";
    public const string ExclusiveListener = "wolverine-listener://rabbitmq/orders";
    public const string LeaderPinnedListener = "wolverine-leader-listener://rabbitmq/inventory";
    public const string RecurringSchedules = "wolverine-recurring://";
    public const string RecurringTimer = "wolverine-recurring-timer:///heartbeat";
    public const string DynamicListener = "wolverine-dynamic-listener://mqtt/devices";
    public const string CustomAgent = "custom-agents://reports";

    private readonly List<SimulatedNode> _nodes = [];
    private readonly RecordingLogger _log = new();
    private readonly IWolverineObserver _observer = Substitute.For<IWolverineObserver>();

    // Not static: like MessageStoreCollection, whose agent list grows with the tenant databases
    private readonly HarnessFamily _durability =
        new(PersistenceConstants.AgentScheme, NodeWorkloads.Durability, HarnessFamily.DistributeDurability);

    private readonly HarnessFamily _projections =
        new StaticHarnessFamily(EventSubscriptionAgentFamily.SchemeName, NodeWorkloads.Projections, HarnessFamily.DistributeProjections);

    private readonly CustomFamily _custom = new();

    private readonly List<IAgentFamily> _families;

    private int? _leader;
    private NodeAgentController? _controller;
    private NodeAgentController? _heartbeatController;
    private Guid _controllerNodeId;
    private bool _capacityAware;
    private AgentRestrictions _restrictions = new([]);

    public WorkloadCluster()
    {
        _durability.Log = _log;

        _families =
        [
            _projections,
            new StaticHarnessFamily("wolverine-listener", NodeWorkloads.Messaging, (g, s) => g.DistributeEvenly(s)),
            new StaticHarnessFamily("wolverine-leader-listener", NodeWorkloads.Messaging, HarnessFamily.RunEachOnLeader),
            new StaticHarnessFamily(RecurringTimerAgentFamily.SchemeName, NodeWorkloads.Messaging,
                (g, s) => g.DistributeEvenlyWithBlueGreenSemantics(s)),
            new HarnessFamily(DynamicListenerUriEncoding.SchemeName, NodeWorkloads.Messaging, (g, s) => g.DistributeEvenly(s)),
            // A custom family that says nothing about workloads, so it gets the default: Messaging
            _custom,
            new RecurringSchedulesAgent(),
            _durability
        ];
    }

    public AssignmentGrid? Grid => _controller?.LastAssignments;

    public AgentCommands LastCommands { get; private set; } = AgentCommands.Empty;

    // -------------------------------------------------------------------------------------------------------------
    // The cluster

    /// <param name="nodes">e.g. "1 (HTTP only), 2 (Messaging, Projections), 3 (All)"</param>
    [Given("nodes {nodes}")]
    public void Nodes(string nodes)
    {
        foreach (Match match in Regex.Matches(nodes, @"(\d+) \(([^)]*)\)"))
        {
            addNode(int.Parse(match.Groups[1].Value), match.Groups[2].Value.Trim());
        }
    }

    [Given("node {node} leads")]
    public void NodeLeads(int node)
    {
        _leader = node;
    }

    [Given("capacity-aware assignment is on")]
    public void CapacityAwareAssignmentIsOn()
    {
        _capacityAware = true;
    }

    [Given("node {node} is at {load}% load")]
    public void NodeIsAtLoad(int node, int load)
    {
        nodeNumbered(node).LoadFactor = load;
    }

    /// <summary>One of each agent family a Wolverine application can have, on one store.</summary>
    [Given("the application has one agent of every family")]
    public void OneAgentOfEveryFamily()
    {
        _durability.Agents.Add(new Uri(Durability));
        _projections.Agents.Add(new Uri(Projection));
        family("wolverine-listener").Agents.Add(new Uri(ExclusiveListener));
        family("wolverine-leader-listener").Agents.Add(new Uri(LeaderPinnedListener));
        family(RecurringTimerAgentFamily.SchemeName).Agents.Add(new Uri(RecurringTimer));
        family(DynamicListenerUriEncoding.SchemeName).Agents.Add(new Uri(DynamicListener));
        _custom.Agents.Add(new Uri(CustomAgent));
        // The recurring schedules agent is a SingularAgent, which always has its one agent
    }

    /// <summary>
    ///     A database-per-tenant event store: each database gets two projection shards, which the event subscription
    ///     family keeps together on one node, and the message store gets one durability agent per database.
    /// </summary>
    [Given("the event store and the message store spread over databases {databases}")]
    public void SpreadOverDatabases(string databases)
    {
        _projections.MultipleDatabases = true;
        foreach (var database in databases.Split(',').Select(x => x.Trim()))
        {
            _projections.Agents.Add(ProjectionIn(database, "trips"));
            _projections.Agents.Add(ProjectionIn(database, "invoices"));
            _durability.Agents.Add(DurabilityFor(database));
        }
    }

    public static Uri ProjectionIn(string database, string projection) =>
        new($"event-subscriptions://marten/main/localhost.{database}/{projection}/all/v1");

    public static Uri DurabilityFor(string database) => new($"wolverinedb://postgresql/localhost/{database}/wolverine");

    /// <summary>
    ///     A blue/green warm-up node of the next release: it also builds <paramref name="agent" />, a projection
    ///     version no serving node has.
    /// </summary>
    [Given("node {node} warms up the next release, which adds {agent}")]
    public void NodeWarmsUpTheNextRelease(int node, string agent)
    {
        var warmup = nodeNumbered(node);
        warmup.Role = NodeRole.Warmup;
        warmup.ExtraProjections.Add(new Uri(agent));
    }

    /// <summary>Placed there by a leader from before workloads, or before the node was redeployed with them.</summary>
    [Given("{agent} is already running on node {node}")]
    public void AgentIsAlreadyRunning(string agent, int node)
    {
        nodeNumbered(node).Running.Add(new Uri(agent));
    }

    [Given("an operator pinned {agent} to node {node}")]
    public void AnOperatorPinned(string agent, int node)
    {
        _restrictions.PinAgent(new Uri(agent), node);
    }

    // -------------------------------------------------------------------------------------------------------------
    // What happens

    /// <summary>
    ///     One assignment evaluation by the leader, after which every agent starts where the leader put it, as it
    ///     does in a real cluster by the next evaluation.
    /// </summary>
    [When("the leader evaluates the assignments")]
    public async Task TheLeaderEvaluates()
    {
        var leader = leaderNode();
        if (_controller == null || _controllerNodeId != leader.Id)
        {
            _controller = buildController(leader);
            _controllerNodeId = leader.Id;
        }

        foreach (var node in _nodes) node.IsLeader = node == leader;

        var snapshot = _nodes.Select(x => x.ToWolverineNode(this)).ToList();
        LastCommands = await _controller.EvaluateAssignmentsAsync(snapshot, _restrictions);

        // The agents start where the leader put them
        var grid = _controller.LastAssignments!;
        foreach (var node in _nodes)
        {
            node.Running.Clear();
            var placed = grid.Nodes.Single(x => x.NodeId == node.Id).Agents.Select(x => x.Uri);
            node.Running.AddRange(placed);
        }

        SpecLog.Write($"node {leader.Number} led; {LastCommands.Count} command(s)");
        foreach (var node in _nodes)
        {
            SpecLog.Write($"node {node.Number} ({node.Description}) runs {(node.Running.Count == 0 ? "nothing" : node.Running.Select(x => x.ToString()).Join(", "))}");
        }
    }

    [When("node {node} leaves")]
    public void NodeLeaves(int node)
    {
        var leaving = nodeNumbered(node);
        _nodes.Remove(leaving);
        if (_leader == node) _leader = null;
    }

    [When("node {node} ({workloads}) joins")]
    public void NodeJoins(int node, string workloads)
    {
        addNode(node, workloads);
    }

    /// <summary>A node's heartbeat reads the node table; every node does this, leader or not.</summary>
    [When("a node's heartbeat reads the node table")]
    public async Task AHeartbeatReadsTheNodeTable()
    {
        // The same node's controller from one heartbeat to the next, as in a running node, so the latch holds
        var reader = _nodes.First();
        var controller = _heartbeatController ??= buildController(reader);
        await controller.WarnIfNoNodeRunsMessagingAsync(_nodes.Select(x => x.ToWolverineNode(this)).ToList());
    }

    /// <summary>The <see cref="IWolverineObserver" /> event a monitor such as CritterWatch alerts on.</summary>
    [Then("the observer is told that no live node runs Messaging")]
    public void TheObserverIsToldNoNodeRunsMessaging()
    {
        var calls = _observer.ReceivedCalls().Count(x => x.GetMethodInfo().Name == nameof(IWolverineObserver.NoLiveNodeRunsMessaging));
        SpecAssert.Check("times the observer was told", calls, 1);
    }

    [Then("the observer is not told anything about Messaging")]
    public void TheObserverIsNotTold()
    {
        var calls = _observer.ReceivedCalls().Count(x => x.GetMethodInfo().Name == nameof(IWolverineObserver.NoLiveNodeRunsMessaging));
        SpecAssert.Check("times the observer was told", calls, 0);
    }

    // -------------------------------------------------------------------------------------------------------------
    // What the leader decided

    [Then("{agent} runs on node {node}")]
    public void AgentRunsOn(string agent, int node) => runsOn(agent, node);

    [Then("{agent} runs on node {first} or node {second}")]
    public void AgentRunsOnEither(string agent, int first, int second) => runsOnEither(agent, first, second);

    // Not steps, so a step built from them renders as one line
    private void runsOn(string agent, int node)
    {
        SpecAssert.Check(agent, whereRuns(agent), $"node {node}");
    }

    private void runsOnEither(string agent, int first, int second)
    {
        var actual = whereRuns(agent);
        SpecAssert.Check(agent, actual == $"node {first}" || actual == $"node {second}" ? $"node {first} or node {second}" : actual,
            $"node {first} or node {second}");
    }

    [Then("{agent} is unplaced: {reason}")]
    public void AgentIsUnplaced(string agent, UnplacedReason reason)
    {
        var found = Grid!.AllAgents.FirstOrDefault(x => x.Uri == new Uri(agent));
        SpecAssert.Check($"{agent} runs on", whereRuns(agent), "no node");
        SpecAssert.Check($"{agent} unplaced because", found?.UnplacedReason?.ToString() ?? "no reason", reason.ToString());
    }

    [Then("node {node} runs nothing")]
    public void NodeRunsNothing(int node)
    {
        var running = nodeNumbered(node).Running;
        SpecAssert.Check($"node {node} runs", running.Count == 0 ? "nothing" : running.Select(x => x.ToString()).Join(", "), "nothing");
    }

    [Then("every projection runs on node {first} or node {second}")]
    public void EveryProjectionRunsOnEither(int first, int second)
    {
        foreach (var projection in _projections.Agents)
        {
            runsOnEither(projection.ToString(), first, second);
        }
    }

    [Then("the projections of database {database} run together on one node")]
    public void TheProjectionsOfADatabaseRunTogether(string database)
    {
        var hosts = _projections.Agents.Where(x => x.ToString().Contains($"localhost.{database}/"))
            .Select(x => whereRuns(x.ToString())).Distinct().ToArray();
        SpecAssert.Check($"nodes running database {database}'s projections", hosts.Join(", "),
            hosts.Length == 1 && hosts[0] != "no node" ? hosts[0] : "one node");
    }

    [Then("the durability agent of database {database} runs on node {node}")]
    public void TheDurabilityAgentOfADatabaseRunsOn(string database, int node)
    {
        runsOn(DurabilityFor(database).ToString(), node);
    }

    [Then("the leader moves {agent} from node {from} to node {to}")]
    public void TheLeaderMoves(string agent, int from, int to)
    {
        var found = Grid!.AllAgents.Single(x => x.Uri == new Uri(agent));
        SpecAssert.Check($"{agent} was running on", numberOf(found.OriginalNode), $"node {from}");
        SpecAssert.Check($"{agent} now runs on", numberOf(found.AssignedNode), $"node {to}");
    }

    [Then("the leader issues no commands")]
    public void TheLeaderIssuesNoCommands()
    {
        SpecAssert.Check("commands", LastCommands.Count == 0 ? "none" : LastCommands.Select(x => x.ToString()!).Join("; "), "none");
    }

    [Then("the leader warns \"{text}\"")]
    public void TheLeaderWarns(string text) => warns(text);

    [Then("it warns \"{text}\"")]
    public void ItWarns(string text) => warns(text);

    [Then("it gives no warning containing \"{text}\"")]
    public void ItDoesNotWarn(string text)
    {
        var found = _log.Warnings.Where(x => x.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        SpecAssert.Check("warnings", found.Length == 0 ? "none" : found.Join(" | "), "none");
    }

    private void warns(string text)
    {
        var found = _log.Warnings.Any(x => x.Contains(text, StringComparison.OrdinalIgnoreCase));
        SpecAssert.Check("a warning saying", found ? text : $"none, among: {(_log.Warnings.Count == 0 ? "no warnings" : _log.Warnings.Join(" | "))}", text);
    }

    // -------------------------------------------------------------------------------------------------------------
    // Leadership, the rule D4 states: a node without Messaging never leads

    [Then("node {node} never leads")]
    public void NodeNeverLeads(int node)
    {
        SpecAssert.Check($"node {node} may lead", nodeNumbered(node).Settings().CanLead, false);
    }

    [Then("node {node} may lead")]
    public void NodeMayLead(int node)
    {
        SpecAssert.Check($"node {node} may lead", nodeNumbered(node).Settings().CanLead, true);
    }

    [Then("no node may lead")]
    public void NoNodeMayLead()
    {
        var eligible = _nodes.Where(x => x.Settings().CanLead).Select(x => $"node {x.Number}").ToArray();
        SpecAssert.Check("nodes that may lead", eligible.Length == 0 ? "none" : eligible.Join(", "), "none");
    }

    // -------------------------------------------------------------------------------------------------------------
    // Plumbing

    private void addNode(int number, string description)
    {
        var node = new SimulatedNode(number, description);
        switch (description.ToLowerInvariant())
        {
            case "http only":
                node.Workloads = NodeWorkloads.None;
                break;
            case "all":
                node.Workloads = NodeWorkloads.All;
                break;
            case "6.x":
                // A node from before workloads: it publishes no marker and holds everything it can
                node.Workloads = NodeWorkloads.All;
                break;
            case "warm-up":
                node.Workloads = NodeWorkloads.All;
                node.Role = NodeRole.Warmup;
                break;
            default:
                node.Workloads = Enum.Parse<NodeWorkloads>(description, true);
                break;
        }

        _nodes.Add(node);
    }

    private HarnessFamily family(string scheme) => _families.OfType<HarnessFamily>().Single(x => x.Scheme == scheme);

    private SimulatedNode nodeNumbered(int number) =>
        _nodes.SingleOrDefault(x => x.Number == number)
        ?? throw new SpecCriticalException($"There is no node {number}; the cluster has {_nodes.Select(x => x.Number.ToString()).Join(", ")}");

    private SimulatedNode leaderNode()
    {
        if (_leader is { } chosen) return nodeNumbered(chosen);

        // What the election comes to: some node that may lead. The lowest number keeps it deterministic
        return _nodes.OrderBy(x => x.Number).FirstOrDefault(x => x.Settings().CanLead)
               ?? throw new SpecCriticalException("No node in this cluster may lead, so there is no leader to evaluate the assignments");
    }

    private string whereRuns(string agent)
    {
        var uri = new Uri(agent);
        var runners = _nodes.Where(x => x.Running.Contains(uri)).Select(x => $"node {x.Number}").ToArray();
        return runners.Length == 0 ? "no node" : runners.Join(", ");
    }

    private string numberOf(AssignmentGrid.Node? node) =>
        node == null ? "no node" : $"node {_nodes.Single(x => x.Id == node.NodeId).Number}";

    private NodeAgentController buildController(SimulatedNode leader)
    {
        var options = new WolverineOptions { ApplicationAssembly = typeof(WorkloadCluster).Assembly };
        options.Transports.NodeControlEndpoint = new FakeEndpoint(new Uri($"fake://node{leader.Number}"), EndpointRole.System);

        // The harness supplies every family, the durability one included; in Balanced mode the controller would
        // add its own listener families over the stand-ins, and with DurabilityAgentEnabled its message store
        options.Durability.Mode = DurabilityMode.Solo;
        options.Durability.DurabilityAgentEnabled = false;
        options.Durability.CapacityAwareAssignment = _capacityAware;
        options.Durability.LeaderColdStartHoldEvaluations = 0;
        options.Durability.LeaderTakeoverHoldEvaluations = 0;

        // The controller is the leader's own, so the leader has to BE the node it runs on
        leader.Id = options.UniqueNodeId;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        runtime.Observer.Returns(_observer);

        return new NodeAgentController(runtime, Substitute.For<INodeAgentPersistence>(), _families, _log,
            CancellationToken.None);
    }

    internal IEnumerable<(IAgentFamily, IReadOnlyList<Uri>)> StaticCapabilitiesFor(SimulatedNode node)
    {
        foreach (var family in _families.OfType<IStaticAgentFamily>())
        {
            // Every static family here is one of the stand-ins, whose supported agents are its agent list
            var supported = family switch
            {
                HarnessFamily harness => harness.Agents.ToList(),
                CustomFamily custom => custom.Agents.ToList(),
                _ => throw new NotSupportedException(family.GetType().Name)
            };

            if (node.Role == NodeRole.Warmup)
            {
                // A warm-up node builds only projections: its other families are removed at startup
                if (family != _projections) continue;
                supported.AddRange(node.ExtraProjections);
            }

            yield return (family, supported);
        }
    }

    internal sealed class SimulatedNode(int number, string description)
    {
        public int Number { get; } = number;
        public string Description { get; } = description;
        public Guid Id { get; set; } = Guid.NewGuid();
        public NodeWorkloads Workloads { get; set; } = NodeWorkloads.All;
        public NodeRole Role { get; set; } = NodeRole.Serving;
        public double? LoadFactor { get; set; }
        public bool IsLeader { get; set; }
        public List<Uri> Running { get; } = [];
        public List<Uri> ExtraProjections { get; } = [];

        public DurabilitySettings Settings() => new()
        {
            Mode = DurabilityMode.Balanced,
            Workloads = Workloads,
            NodeRole = Role
        };

        public WolverineNode ToWolverineNode(WorkloadCluster cluster)
        {
            var node = new WolverineNode
            {
                NodeId = Id,
                AssignedNodeNumber = Number,
                ControlUri = new Uri($"fake://node{Number}"),
                LoadFactor = LoadFactor,
                Description = Description
            };

            // Exactly what this node would advertise at startup
            node.Capabilities.AddRange(NodeAgentController.BuildCapabilities(Settings(), cluster.StaticCapabilitiesFor(this)));

            node.ActiveAgents.AddRange(Running);
            if (IsLeader) node.ActiveAgents.Add(NodeAgentController.LeaderUri);

            return node;
        }
    }

    /// <summary>
    ///     A stand-in for one of Wolverine's agent families, distributing its agents exactly the way the real one does.
    /// </summary>
    internal class HarnessFamily(string scheme, NodeWorkloads required, Action<HarnessFamily, AssignmentGrid> distribute)
        : IAgentFamily
    {
        public HarnessFamily(string scheme, NodeWorkloads required, Action<AssignmentGrid, string> distribute)
            : this(scheme, required, (f, g) => distribute(g, f.Scheme))
        {
        }

        public string Scheme { get; } = scheme;
        public List<Uri> Agents { get; } = [];
        public bool MultipleDatabases { get; set; }
        public ILogger? Log { get; set; }
        private bool _warnedAboutNoCapableNode;

        public NodeWorkloads RequiredWorkload { get; } = required;

        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync() => new(Agents.ToArray());

        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync() => new(Agents.ToArray());

        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
        {
            distribute(this, assignments);
            return ValueTask.CompletedTask;
        }

        /// <summary>LeaderPinnedListenerFamily: every agent on the leader.</summary>
        public static void RunEachOnLeader(AssignmentGrid grid, string scheme)
        {
            foreach (var agent in grid.AgentsForScheme(scheme)) grid.RunOnLeader(agent.Uri);
        }

        /// <summary>EventSubscriptionAgentFamily: database-affine for a multi-database store, else blue/green.</summary>
        public static void DistributeProjections(HarnessFamily family, AssignmentGrid grid)
        {
            if (family.MultipleDatabases)
            {
                grid.DistributeByGroupAffinity(family.Scheme, EventSubscriptionAgentFamily.DatabaseKeyOf);
            }
            else
            {
                grid.DistributeEvenlyWithBlueGreenSemantics(family.Scheme);
            }
        }

        /// <summary>MessageStoreCollection: follow the projections' database, only to capable nodes, and say so when none is.</summary>
        public static void DistributeDurability(HarnessFamily family, AssignmentGrid grid)
        {
            var preference = DurabilityProjectionAffinity.BuildPreference(grid);
            grid.DistributeEvenlyWithAffinity(family.Scheme, preference.NodeFor, MessageStoreCollection.NodeCanRunDurabilityAgents);
            MessageStoreCollection.WarnIfNoCapableNode(grid, family.Log!, ref family._warnedAboutNoCapableNode);
        }
    }

    /// <summary>A family whose nodes advertise its agents at startup, as an <see cref="IStaticAgentFamily" /> does.</summary>
    internal sealed class StaticHarnessFamily : HarnessFamily, IStaticAgentFamily
    {
        public StaticHarnessFamily(string scheme, NodeWorkloads required, Action<AssignmentGrid, string> distribute)
            : base(scheme, required, distribute)
        {
        }

        public StaticHarnessFamily(string scheme, NodeWorkloads required, Action<HarnessFamily, AssignmentGrid> distribute)
            : base(scheme, required, distribute)
        {
        }
    }

    /// <summary>
    ///     A family of the application's own, which never mentions workloads, so it gets the default member of
    ///     <see cref="IAgentFamily.RequiredWorkload" />.
    /// </summary>
    private sealed class CustomFamily : IStaticAgentFamily
    {
        private readonly HarnessFamily _inner = new("custom-agents", NodeWorkloads.Messaging, (g, s) => g.DistributeEvenly(s));

        public List<Uri> Agents => _inner.Agents;
        public string Scheme => _inner.Scheme;
        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync() => _inner.AllKnownAgentsAsync();
        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync() => _inner.SupportedAgentsAsync();
        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => _inner.EvaluateAssignmentsAsync(assignments);
    }

    /// <summary>The recurring schedules agent is a real <see cref="SingularAgent" />.</summary>
    private sealed class RecurringSchedulesAgent() : SingularAgent("wolverine-recurring")
    {
        protected override Task startAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task stopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed class RecordingLogger : ILogger<NodeAgentController>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(message);
                SpecLog.Write($"warning: {message}");
            }
        }
    }
}
