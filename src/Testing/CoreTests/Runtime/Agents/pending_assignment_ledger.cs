using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Regression coverage for the D3/D6 half of the projection-agent churn livelock (GH-3604). A first-time
/// assignment (grid Agent.OriginalNode == null) is emitted as AssignAgent, but a heavily loaded node can
/// take many assignment cycles to actually start each agent and persist its assignment row. Without a
/// pending-assignment ledger the leader re-emits the SAME (agent -> node) AssignAgent every cycle, piling
/// duplicate mega-batches onto the polled control queue and writing ~one AssignmentChanged telemetry row
/// per agent per cycle into the database the rebuild is already saturating. The ledger suppresses those
/// re-emissions until the assignment is confirmed running or the TTL expires.
/// </summary>
public class pending_assignment_ledger
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly INodeAgentPersistence _persistence = Substitute.For<INodeAgentPersistence>();
    private readonly FakeAgentFamily _family = new("fake");
    private readonly NodeAgentController _controller;
    private readonly WolverineNode _self;

    public pending_assignment_ledger()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        _controller = new NodeAgentController(
            _runtime, _persistence, [_family], NullLogger<NodeAgentController>.Instance, CancellationToken.None);

        _self = new WolverineNode
        {
            NodeId = _options.UniqueNodeId,
            AssignedNodeNumber = 1,
            ControlUri = _options.Transports.NodeControlEndpoint!.Uri
        };
        _self.Capabilities.AddRange(_family.AllAgentUris());
    }

    private Task<AgentCommands> evaluateAsync()
        => _controller.EvaluateAssignmentsAsync([_self], new AgentRestrictions());

    private static int assignedAgentCount(AgentCommands commands)
        => commands.OfType<AssignAgents>().Sum(x => x.AgentIds.Length)
           + commands.OfType<AssignAgent>().Count();

    [Fact]
    public async Task suppresses_re_emission_of_the_same_assignment_within_the_ttl()
    {
        // First evaluation: nothing is running anywhere, so every fake agent is assigned to the one node.
        var first = await evaluateAsync();
        assignedAgentCount(first).ShouldBe(FakeAgentFamily.Names.Length);

        // Second evaluation with the SAME still-unconfirmed state: the node hasn't persisted any assignment
        // row yet (ActiveAgents still empty), so pre-fix the leader would re-emit the whole batch. The ledger
        // must now suppress every one of them.
        var second = await evaluateAsync();
        assignedAgentCount(second).ShouldBe(0);
    }

    [Fact]
    public async Task re_emits_after_the_ttl_expires_without_confirmation()
    {
        // Short TTL so the expiry backstop can be exercised with a real delay: 2 x CheckAssignmentPeriod.
        // (Suppression-within-TTL is covered separately with the default 30s period, where no expiry can
        // race the assertions.)
        _options.Durability.CheckAssignmentPeriod = 10.Milliseconds();

        assignedAgentCount(await evaluateAsync()).ShouldBe(FakeAgentFamily.Names.Length);

        await Task.Delay(100.Milliseconds()); // comfortably past the 20ms TTL

        // A start that never took must eventually be re-driven.
        assignedAgentCount(await evaluateAsync()).ShouldBe(FakeAgentFamily.Names.Length);
    }

    [Fact]
    public async Task confirmation_clears_the_ledger_so_a_later_loss_re_emits()
    {
        assignedAgentCount(await evaluateAsync()).ShouldBe(FakeAgentFamily.Names.Length);

        // The node actually started the agents and persisted the assignment rows: they now show up in its
        // ActiveAgents, so the next grid has OriginalNode == AssignedNode == this node. That confirms the
        // pending assignments and clears the ledger (and emits nothing, since they're already where we want).
        _self.ActiveAgents.AddRange(_family.AllAgentUris());
        assignedAgentCount(await evaluateAsync()).ShouldBe(0);

        // Now the node loses them (e.g. a restart): ActiveAgents empty again. Because confirmation cleared
        // the ledger — rather than the entries merely being suppressed — the reassignment is re-emitted
        // immediately instead of waiting out a TTL.
        _self.ActiveAgents.Clear();
        assignedAgentCount(await evaluateAsync()).ShouldBe(FakeAgentFamily.Names.Length);
    }
}
