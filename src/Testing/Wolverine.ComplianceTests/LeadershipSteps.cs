using System.Text.RegularExpressions;
using Bobcat;
using Bobcat.Engine;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.ComplianceTests.Telemetry;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
///     One Wolverine host in a <see cref="LeadershipElectionCompliance" /> cluster. Renders as "node N" in step text,
///     where N is the number Wolverine assigned it, which is the number every log line, every assignment row and the
///     agent-assignment timeline use.
/// </summary>
public sealed class ClusterNode
{
    // Read once the host has started, which is when the number is assigned, and kept: a stopped node's host is
    // disposed, and it still has to render as itself in the steps that come after
    internal ClusterNode(IHost host)
    {
        Host = host;
        Number = host.GetRuntime().Options.Durability.AssignedNodeNumber;
        Id = host.GetRuntime().Options.UniqueNodeId;
    }

    public IHost Host { get; }

    public int Number { get; }

    public Guid Id { get; }

    public override string ToString() => $"node {Number}";

    /// <summary>
    ///     Steps take a node by its number, because Bobcat renders a step argument it does not know by its type's
    ///     name ("ClusterNode stops") rather than its <c>ToString()</c>. This keeps the call sites
    ///     <c>the.NodeStops(second)</c> while the step text reads "node 2 stops".
    /// </summary>
    public static implicit operator int(ClusterNode node) => node.Number;
}

/// <summary>
///     How many agents each node should end up running, written as an object initializer --
///     <c>new Placement { [first] = 6, [second] = 6 }</c> -- and rendered in step text as
///     "node 1 runs 6, node 2 runs 6".
/// </summary>
public sealed class Placement
{
    private readonly List<(ClusterNode Node, int Count)> _expected = new();

    public int this[ClusterNode node]
    {
        get => _expected.Single(x => x.Node == node).Count;
        set
        {
            _expected.RemoveAll(x => x.Node == node);
            _expected.Add((node, value));
        }
    }

    public IReadOnlyList<(ClusterNode Node, int Count)> Expected => _expected;

    public override string ToString() => _expected.Select(x => $"{x.Node} runs {x.Count}").Join(", ");

    /// <summary>Passed to a step as its text, for the same reason <see cref="ClusterNode" /> is passed as a number.</summary>
    public static implicit operator string(Placement placement) => placement.ToString();
}

/// <summary>
///     The leadership suite's vocabulary. These were private helpers and inline waits on
///     <see cref="LeadershipElectionCompliance" />; a step helper has to be callable from the interceptor Bobcat
///     generates, so they are public here, on a non-generic class the base holds (a private step is BOBCAT039).
/// </summary>
/// <remarks>
///     <para>
///         <b>Every step attaches the timeline</b>, because xUnit v3 runs <c>IAsyncLifetime.InitializeAsync</c> --
///         where the first node starts -- before the scenario's recording opens, so <c>SpecLog</c>,
///         <c>SpecReport</c> and <c>SpecAttachment</c> are silent no-ops there. The first node's events are not lost:
///         the telemetry is already buffering them.
///     </para>
///     <para>
///         <b>Waits gather, then stop.</b> When a wait gives up it records one check per thing it was waiting for
///         (so every wrong node is reported, not the first one Shouldly threw on) plus the placement report, and then
///         throws <see cref="SpecCriticalException" />: the rest of a cluster scenario depends on the cluster having
///         settled, and carrying on would only wait out every later timeout and overwrite the placement report with
///         a less useful one.
///     </para>
/// </remarks>
public sealed class LeadershipSteps : IAsyncDisposable
{
    public static readonly TimeSpan PollingInterval = 250.Milliseconds();

    private readonly Type _suite;
    private readonly Action<WolverineOptions> _configureNode;
    private readonly ITestOutputHelper _output;
    private readonly List<ClusterNode> _nodes = new();

    public LeadershipSteps(Type suite, Action<WolverineOptions> configureNode, ITestOutputHelper output)
    {
        _suite = suite;
        _configureNode = configureNode;
        _output = output;
    }

    public AgentAssignmentTelemetry Telemetry { get; } = new();

    /// <summary>How long a wait for the leadership gives the cluster.</summary>
    public TimeSpan LeadershipTimeout { get; set; } = 30.Seconds();

    /// <summary>How long a wait for the agents to settle gives the cluster.</summary>
    public TimeSpan SettleTimeout { get; set; } = 60.Seconds();

    public IReadOnlyList<ClusterNode> Nodes => _nodes;

    public IReadOnlyList<IHost> Hosts => _nodes.Select(x => x.Host).ToArray();

    public static Uri[] AllFakeAgents => FakeAgentFamily.Names.Select(x => new Uri($"fake://{x}")).ToArray();

    public static Uri SingularAgent { get; } = new("simple://");

    public static void ConfigureSlowHeartbeat(WolverineOptions o)
    {
        o.Durability.HealthCheckPollingTime = 10.Minutes();
        o.Durability.StaleNodeTimeout = 15.Minutes();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Nodes

    /// <summary>Not a step: <c>InitializeAsync</c> calls this for the first node, before any recording is open.</summary>
    public async Task<ClusterNode> StartNodeAsync(Action<WolverineOptions>? configure = null)
    {
        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeAssembly(_suite.Assembly).IncludeType(typeof(DoOnAgentHandler))
                    .IncludeType(typeof(TriggerFanOutHandler)).IncludeType(typeof(FanOutHandler))
                    .IncludeType(typeof(BroadcastPingHandler));

                opts.Durability.CheckAssignmentPeriod = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();

                opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();

                _configureNode(opts);
                configure?.Invoke(opts);
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.Services.AddResourceSetupOnStartup();

                #region sample_register_singular_agent
                // Little extension method helper on IServiceCollection to register your
                // SingularAgent
                opts.Services.AddSingularAgent<SimpleSingularAgent>();

                #endregion
            }).Build();

        // Before StartAsync: the node controller captures the runtime's observer when the host starts
        Telemetry.Watch(host.GetRuntime());

        await host.StartAsync();

        new XUnitEventObserver(host, _output);

        var node = new ClusterNode(host);
        _nodes.Add(node);

        SpecLog.Write($"{node} started as {node.Id}");
        return node;
    }

    [Given("a node starts")]
    public Task<ClusterNode> ANodeStarts()
    {
        Telemetry.AttachToScenario();
        return StartNodeAsync();
    }

    [Given("a node starts with a ten-minute heartbeat")]
    public Task<ClusterNode> ANodeStartsWithASlowHeartbeat()
    {
        Telemetry.AttachToScenario();
        return StartNodeAsync(ConfigureSlowHeartbeat);
    }

    [Given("a node that is not eligible for leadership starts, with a ten-minute heartbeat")]
    public Task<ClusterNode> AnIneligibleNodeStarts()
    {
        Telemetry.AttachToScenario();
        return StartNodeAsync(o =>
        {
            ConfigureSlowHeartbeat(o);
            o.Durability.LeadershipEligible = false;
        });
    }

    [Given("a blue/green warm-up node starts, with a ten-minute heartbeat")]
    public Task<ClusterNode> AWarmupNodeStarts()
    {
        Telemetry.AttachToScenario();
        return StartNodeAsync(o =>
        {
            ConfigureSlowHeartbeat(o);
            o.Durability.NodeRole = NodeRole.Warmup;
        });
    }

    [When("node {node} stops")]
    public async Task NodeStops(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();

        var wasLeader = subject.Host.GetRuntime().IsLeader();
        Telemetry.Note(subject.Number, $"stopping {subject}");
        await stopAsync(subject);

        SpecLog.Write($"{subject} stopped{(wasLeader ? ", taking the leadership with it" : "")}");
    }

    [When("node {node} goes stale")]
    public async Task NodeGoesStale(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();
        Telemetry.Note(subject.Number, $"{subject} disables its agents, with a heartbeat an hour old");

        await subject.Host.GetRuntime().DisableAgentsAsync(DateTimeOffset.UtcNow.AddHours(-1));
    }

    [When("node {node} crashes: its lock is released, but its heartbeat and assignment rows are fresh")]
    public async Task NodeCrashesInsideTheStaleWindow(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();
        Telemetry.Note(subject.Number, $"{subject} crashes inside the stale window");

        await subject.Host.GetRuntime().DisableAgentsAsync(DateTimeOffset.UtcNow);
    }

    [When("node {node} is prodded to check agent health")]
    public async Task NodeChecksAgentHealth(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();
        Telemetry.Note(subject.Number, $"{subject} is sent CheckAgentHealth");

        await subject.Host.InvokeMessageAndWaitAsync(new CheckAgentHealth());
    }

    [When("node {node} stops running health checks")]
    public void NodeStopsRunningHealthChecks(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();
        Telemetry.Note(subject.Number, $"{subject}'s health checks are disabled");

        subject.Host.GetRuntime().Agents.DisableHealthChecks();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Leadership

    [Then("node {node} becomes the leader")]
    public async Task NodeBecomesTheLeader(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();

        var became = await pollAsync(() => subject.Host.GetRuntime().IsLeader(), describeLeadership,
            LeadershipTimeout);

        SpecAssert.Check("leader", currentLeader(), subject.ToString());
        stopUnless(became, $"{subject} did not assume the leadership");
    }

    [Then("a node other than node {former} becomes the leader")]
    public async Task AnotherNodeBecomesTheLeader(int former)
    {
        var formerNode = nodeNumbered(former);

        Telemetry.AttachToScenario();

        var became = await pollAsync(() => _nodes.Any(x => x != formerNode && x.Host.GetRuntime().IsLeader()),
            describeLeadership, LeadershipTimeout);

        SpecAssert.Fact(became, $"no node other than {formerNode} assumed the leadership");
        stopUnless(became, $"no node other than {formerNode} assumed the leadership");
    }

    [Then("node {node} is not the leader")]
    public void NodeIsNotTheLeader(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();
        SpecAssert.Check($"{subject} leads", subject.Host.GetRuntime().IsLeader(), false);
    }

    [Then("node {node} is the only node with a persisted leader row")]
    public async Task NodeIsTheOnlyPersistedLeader(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();

        var state = await subject.Host.GetRuntime().Storage.Nodes.LoadNodeAgentStateAsync(CancellationToken.None);
        var leaders = state.Nodes.Where(x => x.IsLeader()).Select(x => x.AssignedNodeNumber).OrderBy(x => x)
            .ToArray();

        SpecAssert.Check("persisted leader rows", leaders.Length == 0 ? "none" : leaders.Select(x => $"node {x}").Join(", "),
            subject.ToString());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Agent placement

    /// <summary>
    ///     <c>WaitUntilAssignmentsChangeTo(w => w.ExpectRunningAgents(host, n)…)</c>. Narrates every change it sees
    ///     while it waits as a step log, and on timeout records one check per node plus the placement report,
    ///     instead of a string in a <c>TimeoutException</c>.
    /// </summary>
    /// <param name="placement">A <see cref="Placement" />, as its text: "node 1 runs 6, node 2 runs 6".</param>
    [Then("the agents settle as {placement}")]
    public async Task AgentsSettleAs(string placement)
    {
        Telemetry.AttachToScenario();

        var expected = Regex.Matches(placement, @"node (\d+) runs (\d+)")
            .Select(m => (Node: nodeNumbered(int.Parse(m.Groups[1].Value)), Count: int.Parse(m.Groups[2].Value)))
            .ToArray();

        Dictionary<ClusterNode, int> counts() =>
            expected.ToDictionary(x => x.Node, x => ordinaryAgentsOn(x.Node).Length);

        var settled = await pollAsync(() => expected.All(x => counts()[x.Node] == x.Count),
            () => counts().Select(x => $"{x.Key} runs {x.Value}").Join(", "), SettleTimeout);

        if (settled) return;

        await AgentPlacementReport.CaptureAsync(Hosts);

        var final = counts();
        foreach (var (node, count) in expected)
        {
            SpecAssert.Check($"{node}", final[node], count);
        }

        stopUnless(false, $"the agents did not settle as {placement}");
    }

    [Then("every agent runs exactly once")]
    public Task EveryAgentRunsExactlyOnce() => expectExactlyOneCopyOfEachAsync(AllFakeAgents, []);

    [Then("every agent but {paused} runs exactly once, and {paused} runs nowhere")]
    public Task EveryAgentButThePausedOneRunsExactlyOnce(string paused)
    {
        var pausedUri = new Uri(paused);
        return expectExactlyOneCopyOfEachAsync(AllFakeAgents.Where(x => x != pausedUri).ToArray(), [pausedUri]);
    }

    /// <summary>
    ///     Polls until every agent in <paramref name="expected" /> is running on exactly one node and nothing in
    ///     <paramref name="stopped" /> is running anywhere. On timeout the three failure modes are three checks, all
    ///     reported: a MISSING agent means a stop was issued without a start reaching anyone, a DUPLICATED one means a
    ///     start was issued without a stop.
    /// </summary>
    private async Task expectExactlyOneCopyOfEachAsync(Uri[] expected, Uri[] stopped)
    {
        Telemetry.AttachToScenario();

        Uri[] running() => _nodes.SelectMany(x => x.Host.RunningAgents()).ToArray();

        var settled = await pollAsync(() =>
            {
                var now = running();
                return expected.All(uri => now.Count(x => x == uri) == 1) && stopped.All(uri => !now.Contains(uri));
            },
            () =>
            {
                var now = running();
                var once = expected.Count(uri => now.Count(x => x == uri) == 1);
                var text = $"{once} of {expected.Length} agents running exactly once";
                var stray = stopped.Count(uri => now.Contains(uri));
                return stray > 0 ? $"{text}, {stray} paused agent(s) running" : text;
            }, SettleTimeout);

        if (settled) return;

        await AgentPlacementReport.CaptureAsync(Hosts, stopped);

        var final = running();
        SpecAssert.Check("agents running on more than one node",
            names(final.Where(expected.Contains).GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key)),
            "none");
        SpecAssert.Check("agents not running anywhere", names(expected.Where(uri => !final.Contains(uri))), "none");
        SpecAssert.Check("paused agents that are running", names(stopped.Where(final.Contains)), "none");

        stopUnless(false, "the agents did not settle on exactly one copy each");
    }

    /// <summary>
    ///     Polls until <paramref name="agent" /> runs on exactly one node AND its assignment row names that node.
    ///     Deliberately does not say WHICH node: the leader is free to re-place or rebalance an agent while a
    ///     divergence heals, and does.
    /// </summary>
    [Then("{agent} runs once, with a row naming its node")]
    public async Task AgentRunsOnceWithARowNamingItsNode(string agent)
    {
        var agentUri = new Uri(agent);

        Telemetry.AttachToScenario();

        async Task<(ClusterNode[] Runners, int? Row)> observe()
        {
            var runners = _nodes.Where(x => x.Host.RunningAgents().Contains(agentUri)).ToArray();
            return (runners, await rowOwnerOfAsync(agentUri));
        }

        static string describe((ClusterNode[] Runners, int? Row) x) =>
            $"running on {(x.Runners.Length == 0 ? "no node" : x.Runners.Select(n => n.ToString()).Join(", "))}, row names {(x.Row is { } n ? $"node {n}" : "no node")}";

        var settled = await pollAsync(async () =>
        {
            var now = await observe();
            return now.Runners.Length == 1 && now.Row == now.Runners[0].Number;
        }, async () => describe(await observe()), SettleTimeout);

        if (settled) return;

        await AgentPlacementReport.CaptureAsync(Hosts);

        var final = await observe();
        SpecAssert.Check("copies running", final.Runners.Length, 1);
        if (final.Runners.Length == 1)
        {
            SpecAssert.Check("row names", final.Row is { } row ? $"node {row}" : "no node",
                final.Runners[0].ToString());
        }

        stopUnless(false, $"{agentUri} did not converge to one copy with a row naming its node");
    }

    [Then("the singular agent runs on exactly one node")]
    public async Task TheSingularAgentRunsOnExactlyOneNode()
    {
        Telemetry.AttachToScenario();

        string[] runners() => _nodes.Where(x => x.Host.RunningAgents().Contains(SingularAgent))
            .Select(x => x.ToString()).ToArray();

        await pollAsync(() => runners().Length > 0,
            () => runners().Length == 0 ? "running nowhere" : $"running on {runners().Join(", ")}", 15.Seconds());

        SpecAssert.Check("copies running", runners().Length, 1);
    }

    [Then("node {node} still runs {agent}")]
    public void NodeRuns(int node, string agent)
    {
        var subject = nodeNumbered(node);
        var agentUri = new Uri(agent);

        Telemetry.AttachToScenario();
        SpecAssert.Check($"{subject} runs {agentUri}", subject.Host.RunningAgents().Contains(agentUri), true);
    }

    /// <summary>Not a step: picks the subject of a scenario.</summary>
    public ClusterNode NodeRunning(Uri agent) => _nodes.First(x => x.Host.RunningAgents().Contains(agent));

    /// <summary>Not a step: picks the subject of a scenario.</summary>
    public ClusterNode NodeRunning(string agent) => NodeRunning(new Uri(agent));

    /// <summary>Not a step: picks the subject of a scenario, as the text the agent steps take.</summary>
    public string AnAgentRunningOn(ClusterNode node)
        => node.Host.GetRuntime().Agents.AllRunningAgentUris().First().ToString();

    // ---------------------------------------------------------------------------------------------------------
    // Pausing

    [When("{agent} is paused through node {node}")]
    public async Task AgentIsPaused(string agent, int node)
    {
        var agentUri = new Uri(agent);
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();
        Telemetry.Note(subject.Number, $"pause {agentUri} through {subject}");

        var restrictions = new AgentRestrictions([]);
        restrictions.PauseAgent(agentUri);
        await subject.Host.GetRuntime().Agents.ApplyRestrictionsAsync(restrictions, CancellationToken.None);
    }

    /// <summary>
    ///     No polling: by the time <c>ApplyRestrictionsAsync</c> returns the stop must have been carried out, and the
    ///     in-memory restrictions <c>ListeningAgent</c> consults must say so, or a paused listener restarts itself.
    /// </summary>
    [Then("{agent} has stopped on node {node}, which knows it is paused")]
    public void AgentHasStoppedAndIsKnownPaused(string agent, int node)
    {
        var agentUri = new Uri(agent);
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();

        var runtime = subject.Host.GetRuntime();
        SpecAssert.Check("still running", runtime.Agents.AllRunningAgentUris().Contains(agentUri), false);
        SpecAssert.Check("known paused", runtime.Restrictions.FindPausedAgentUris().Contains(agentUri), true);
    }

    // ---------------------------------------------------------------------------------------------------------
    // GH-4407: the divergences the GH-3987 reconciliation sweep exists to heal, fabricated on a real cluster

    /// <summary>
    ///     The leadership-handover duplicate: a second copy comes up on a node the table does not name. Starting it
    ///     there upserts the intruder's row, so the owner's claim is put straight back, leaving the intruder running a
    ///     copy no row accounts for.
    /// </summary>
    [When("node {intruder} starts a second copy of {agent} that no row accounts for")]
    public async Task ANodeStartsACopyNoRowAccountsFor(int intruder, string agent)
    {
        var intruderNode = nodeNumbered(intruder);
        var agentUri = new Uri(agent);

        Telemetry.AttachToScenario();

        var owner = NodeRunning(agentUri);
        Telemetry.Note(intruderNode.Number, $"{intruderNode} starts {agentUri}; {owner}'s row for it is put back");

        await intruderNode.Host.GetRuntime().NodeController!.StartAgentAsync(agentUri);
        await owner.Host.GetRuntime().Storage.Nodes.AddAssignmentAsync(owner.Id, agentUri, CancellationToken.None);
    }

    /// <summary>The GH-3987 wedge: the agent goes away on its owner while the owner's row stays put.</summary>
    [When("{agent} stops on node {owner} without its row being removed")]
    public async Task AnAgentStopsLeavingItsRow(string agent, int owner)
    {
        var agentUri = new Uri(agent);
        var ownerNode = nodeNumbered(owner);

        Telemetry.AttachToScenario();
        Telemetry.Note(ownerNode.Number, $"{agentUri} is pulled out of {ownerNode}'s controller; the row stays");

        var removed = ownerNode.Host.GetRuntime().NodeController!.Agents.TryRemove(agentUri, out var running);
        SpecAssert.Check($"{ownerNode} was running {agentUri}", removed, true);
        if (running != null) await running.StopAsync(CancellationToken.None);
    }

    /// <summary>The peer-ejection cascade: the row goes, the running copy stays.</summary>
    [When("node {owner}'s row for {agent} is lost while it keeps running")]
    public async Task AnAgentsRowIsLost(int owner, string agent)
    {
        var ownerNode = nodeNumbered(owner);
        var agentUri = new Uri(agent);

        Telemetry.AttachToScenario();
        Telemetry.Note(ownerNode.Number, $"{ownerNode}'s row for {agentUri} is deleted");

        await ownerNode.Host.GetRuntime().Storage.Nodes.RemoveAssignmentAsync(ownerNode.Id, agentUri, CancellationToken.None);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Node records

    [When("node {node} logs four node records")]
    public Task NodeLogsRecords(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();

        var records = new[]
        {
            new NodeRecord { Description = "one", NodeNumber = 1, RecordType = NodeRecordType.NodeStarted },
            new NodeRecord { Description = "one", NodeNumber = 1, RecordType = NodeRecordType.NodeStopped },
            new NodeRecord { Description = "one", NodeNumber = 2, RecordType = NodeRecordType.NodeStarted },
            new NodeRecord { Description = "one", NodeNumber = 2, RecordType = NodeRecordType.NodeStopped }
        };

        return subject.Host.GetRuntime().Storage.Nodes.LogRecordsAsync(records);
    }

    /// <summary>
    ///     Bounded: the attempt counter was once never incremented, so a store that did not persist the records spun
    ///     here forever -- a 40-minute hang on the SharedMemory variant -- instead of failing.
    /// </summary>
    [Then("node {node} reads back at least four node records")]
    public async Task NodeReadsBackRecords(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();

        var nodes = subject.Host.GetRuntime().Storage.Nodes;
        await SpecAssert.EventuallyAsync("records read back",
            async () => Math.Min((await nodes.FetchRecentRecordsAsync(10)).Count, 4), 4,
            10 * PollingInterval, PollingInterval);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Messaging across the cluster

    [When("node {node} fans a message out to all nodes")]
    public async Task NodeFansOut(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();

        // TriggerFanOut's handler calls FanOutToAllNodes(new FannedOut())
        await subject.Host.InvokeMessageAndWaitAsync(new TriggerFanOut());
    }

    /// <summary>Control queue messages are not tracked, so this polls.</summary>
    [Then("every node handles the fanned-out message")]
    public async Task EveryNodeHandlesTheFannedOutMessage()
    {
        Telemetry.AttachToScenario();

        await pollAsync(() => _nodes.All(n => FanOutHandler.Handled.Contains(n.Number)),
            () => $"handled on {handledOnText(FanOutHandler.Handled)}", 15.Seconds());

        var handled = FanOutHandler.Handled.ToHashSet();
        foreach (var node in _nodes)
        {
            SpecAssert.Check($"handled on {node}", handled.Contains(node.Number), true);
        }
    }

    [When("node {node} broadcasts {ping} to all nodes, from outside any handler")]
    public Task NodeBroadcasts(int node, BroadcastPing ping)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();

        // From outside of any message handler, the way an HTTP endpoint or a background service would
        return subject.Host.MessageBus().BroadcastToAllNodesAsync(ping).AsTask();
    }

    [Then("every node handles {ping} exactly once")]
    public async Task EveryNodeHandlesThePingExactlyOnce(BroadcastPing ping)
    {
        Telemetry.AttachToScenario();

        int[] handledBy() => BroadcastPingHandler.Handled.Where(x => x.Id == ping.Id).Select(x => x.NodeNumber)
            .ToArray();

        // Control queue messages are not tracked, so poll
        await pollAsync(() => _nodes.All(n => handledBy().Contains(n.Number)),
            () => $"handled on {handledOnText(handledBy())}", 15.Seconds());

        // Give a duplicate delivery the chance to show itself before checking "exactly once"
        await Task.Delay(1.Seconds());

        var final = handledBy();
        foreach (var node in _nodes)
        {
            SpecAssert.Check($"times handled on {node}", final.Count(x => x == node.Number), 1);
        }

        var strangers = final.Where(x => _nodes.All(n => n.Number != x)).Distinct().ToArray();
        SpecAssert.Check("handled on nodes outside the cluster", strangers.Length == 0 ? "none" : handledOnText(strangers),
            "none");
    }

    [When("node {node} is asked to act on every agent")]
    public async Task NodeActsOnEveryAgent(int node)
    {
        var subject = nodeNumbered(node);

        Telemetry.AttachToScenario();

        foreach (var agent in new FakeAgentFamily().AllAgentUris())
        {
            await subject.Host.InvokeMessageAndWaitAsync(new DoOnAgent(agent));
        }
    }

    /// <summary>
    ///     A <see cref="DoOnAgent" /> invoked against the wrong node is FORWARDED over the control queue, and that leg
    ///     is outside the tracked session, so this polls; the old 1-second ceiling lost the race to any forwarding
    ///     slower than a second. One check per agent: the node that handled it against the nodes running it, where
    ///     the old assertion stopped at the first "Sequence contains no matching element".
    /// </summary>
    [Then("each agent's command was handled on the node running it")]
    public async Task EachAgentsCommandWasHandledWhereItRuns()
    {
        Telemetry.AttachToScenario();

        var agents = new FakeAgentFamily().AllAgentUris();

        await pollAsync(() => agents.All(a => DoOnAgentHandler.Handled.Any(x => x.AgentUri == a)),
            () => $"{agents.Count(a => DoOnAgentHandler.Handled.Any(x => x.AgentUri == a))} of {agents.Length} handled",
            20.Seconds());

        foreach (var agent in agents)
        {
            var handledOn = DoOnAgentHandler.Handled.Where(x => x.AgentUri == agent).Select(x => $"node {x.NodeNumber}")
                .Distinct().ToArray();
            var runningOn = _nodes.Where(x => x.Host.RunningAgents().Contains(agent)).Select(x => x.ToString())
                .ToArray();

            SpecAssert.Check(agent.ToString(), handledOn.Length == 0 ? "never handled" : handledOn.Join(", "),
                runningOn.Length == 0 ? "running nowhere" : runningOn.Join(", "));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Plumbing

    private ClusterNode nodeNumbered(int number)
        => _nodes.SingleOrDefault(x => x.Number == number)
           ?? throw new SpecCriticalException($"No running node is numbered {number}; the cluster has {(_nodes.Count == 0 ? "no nodes" : _nodes.Select(x => x.ToString()).Join(", "))}");

    private static string names(IEnumerable<Uri> uris)
    {
        var list = uris.Select(x => x.ToString()).OrderBy(x => x).ToArray();
        return list.Length == 0 ? "none" : list.Join(", ");
    }

    private static string handledOnText(IEnumerable<int> numbers)
    {
        var list = numbers.Distinct().OrderBy(x => x).Select(x => $"node {x}").ToArray();
        return list.Length == 0 ? "no node" : list.Join(", ");
    }

    // The same filter AssignmentWaiter.HasReached counts with: Wolverine's own agents and the singular agent aside
    private static Uri[] ordinaryAgentsOn(ClusterNode node) => node.Host.RunningAgents()
        .Where(x => !x.Scheme.StartsWith("wolverine") && !x.Scheme.EqualsIgnoreCase("simple")).ToArray();

    private string currentLeader()
    {
        var leaders = _nodes.Where(x => x.Host.GetRuntime().IsLeader()).Select(x => x.ToString()).ToArray();
        return leaders.Length == 0 ? "none" : leaders.Join(", ");
    }

    private string describeLeadership() => $"leader: {currentLeader()}";

    private async Task<int?> rowOwnerOfAsync(Uri agent)
    {
        var nodes = await _nodes.First().Host.GetRuntime().Storage.Nodes.LoadAllNodesAsync(CancellationToken.None);
        return nodes.FirstOrDefault(x => x.ActiveAgents.Contains(agent))?.AssignedNodeNumber;
    }

    private static void stopUnless(bool condition, string why)
    {
        if (!condition) throw new SpecCriticalException($"{why}; the rest of the scenario depends on it");
    }

    private Task<bool> pollAsync(Func<bool> done, Func<string> describe, TimeSpan timeout)
        => pollAsync(() => Task.FromResult(done()), () => Task.FromResult(describe()), timeout);

    /// <summary>
    ///     Poll until <paramref name="done" />, logging <paramref name="describe" /> each time what it says changes --
    ///     a handful of lines per wait, not one per poll, which is what keeps a step under Bobcat's step-log cap.
    /// </summary>
    private async Task<bool> pollAsync(Func<Task<bool>> done, Func<Task<string>> describe, TimeSpan timeout)
    {
        var started = Telemetry.Elapsed;
        string? last = null;

        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            var now = await describe();
            if (now != last)
            {
                SpecLog.Write($"{(Telemetry.Elapsed - started).TotalSeconds:0.00}s: {now}");
                last = now;
            }

            if (await done()) return true;
            if (cancellation.IsCancellationRequested) break;

            await Task.Delay(PollingInterval);
        }

        SpecLog.Write($"gave up after {timeout.TotalSeconds:0.#}s");
        return false;
    }

    private async Task stopAsync(ClusterNode node)
    {
        node.Host.GetRuntime().Agents.DisableHealthChecks();
        await node.Host.StopAsync();
        node.Host.Dispose();
        _nodes.Remove(node);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            node.Host.GetRuntime().Agents.DisableHealthChecks();
        }

        foreach (var node in _nodes.AsEnumerable().Reverse().ToArray())
        {
            await stopAsync(node);
        }
    }
}
