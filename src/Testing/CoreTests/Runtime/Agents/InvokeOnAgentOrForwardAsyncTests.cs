using System.Collections.Concurrent;
using CoreTests.Runtime;
using JasperFx;
using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public interface ITypedAgent : IAgent
{
    bool WasInvoked { get; }
    void MarkInvoked();
}

public class TypedFakeAgent : ITypedAgent
{
    public TypedFakeAgent(Uri uri)
    {
        Uri = uri;
    }

    public Uri Uri { get; }
    public AgentStatus Status { get; private set; } = AgentStatus.Running;
    public bool WasInvoked { get; private set; }

    public void MarkInvoked()
    {
        WasInvoked = true;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Status = AgentStatus.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Status = AgentStatus.Stopped;
        return Task.CompletedTask;
    }
}

public class invoke_on_typed_agent_or_forward_tests
{
    private readonly MockWolverineRuntime _runtime = new();
    private readonly Uri _agentUri = new("typedfake://alpha");

    [Fact]
    public async Task should_invoke_action_on_correctly_typed_local_agent()
    {
        // Arrange: agent is running locally and is the correct type
        var typedAgent = new TypedFakeAgent(_agentUri);

        _runtime.Agents.AllRunningAgentUris().Returns([_agentUri]);
        _runtime.Agents.TryFindActiveAgent(_agentUri, out Arg.Any<ITypedAgent>())
            .Returns(x =>
            {
                x[1] = typedAgent;
                return true;
            });

        var context = new MessageContext(_runtime);
        var actionCalled = false;

        // Act
        var result = await context.InvokeOnAgentOrForwardAsync<ITypedAgent>(_agentUri, agent =>
        {
            actionCalled = true;
            agent.MarkInvoked();
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
        actionCalled.ShouldBeTrue();
        typedAgent.WasInvoked.ShouldBeTrue();
    }

    [Fact]
    public async Task should_not_invoke_action_when_agent_type_does_not_match()
    {
        // Arrange: agent is running locally but is NOT the expected type
        _runtime.Agents.AllRunningAgentUris().Returns([_agentUri]);
        _runtime.Agents.TryFindActiveAgent(_agentUri, out Arg.Any<ITypedAgent>())
            .Returns(x =>
            {
                x[1] = null!;
                return false;
            });

        var context = new MessageContext(_runtime);
        var actionCalled = false;

        // Act
        var result = await context.InvokeOnAgentOrForwardAsync<ITypedAgent>(_agentUri, _ =>
        {
            actionCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert: method returns true (agent was local) but action was NOT invoked
        result.ShouldBeTrue();
        actionCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task should_return_false_when_agent_not_found_anywhere()
    {
        // Arrange: agent is not running on any node
        _runtime.Agents.AllRunningAgentUris().Returns(Array.Empty<Uri>());
        _runtime.Storage.Nodes.LoadAllNodesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WolverineNode>());

        var context = new MessageContext(_runtime);
        var actionCalled = false;

        // Act
        var result = await context.InvokeOnAgentOrForwardAsync<ITypedAgent>(_agentUri, _ =>
        {
            actionCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        result.ShouldBeFalse();
        actionCalled.ShouldBeFalse();
    }
}

/// <summary>
/// CritterWatch#1171 — the two questions this routing asks are answered by two different sources, and
/// nothing handled them disagreeing.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Run it here?"</b> is answered by the in-process <c>NodeController.Agents</c> dictionary
/// (<see cref="IAgentRuntime.AllRunningAgentUris"/>). <b>"Forward it where?"</b> is answered by the
/// durable <c>wolverine_nodes</c> table. During a startup window the table is already populated while
/// the dictionary is still empty — so on a single-node service the envelope was sent to
/// <c>node.ControlUri</c>, which is this node, where it took the same branch again. The method
/// returned <c>true</c> for that, and every caller reads <c>true</c> as "done".
/// </para>
/// <para>
/// Proven downstream by sampling both sources every 250 ms across 10 isolated runs: 2 failed, and one
/// variable separated them — the agent was running on all 8 passes and on neither failure. The
/// commands were acked as successful and the daemon never acted, which read as a daemon defect for
/// weeks because the URI resolution that precedes this goes through the store's shard REGISTRY and
/// answers happily either way.
/// </para>
/// </remarks>
public class no_self_forward_when_the_node_table_and_the_agent_dictionary_disagree
{
    private readonly MockWolverineRuntime _runtime = new();
    private readonly Uri _agentUri = new("typedfake://alpha");

    /// <summary>The node table claims the agent for THIS node while it is not running here.</summary>
    private void TableClaimsAgentFor(Guid nodeId, Uri? controlUri)
    {
        _runtime.Agents.AllRunningAgentUris().Returns(Array.Empty<Uri>());
        _runtime.Storage.Nodes.LoadAllNodesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WolverineNode>
            {
                new() { NodeId = nodeId, ControlUri = controlUri, ActiveAgents = { _agentUri } }
            });
    }

    [Fact]
    public async Task does_not_forward_to_itself_and_says_so()
    {
        TableClaimsAgentFor(_runtime.Options.UniqueNodeId, new Uri("dbcontrol://one"));

        var context = new MessageContext(_runtime);
        var actionCalled = false;

        var outcome = await context.InvokeOnAgentAsync(_agentUri, (_, _) =>
        {
            actionCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        outcome.ShouldBe(AgentInvocationOutcome.NotRunningLocally);
        actionCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task the_bool_overload_reports_that_as_a_failure()
    {
        // The whole point. It used to answer `true` here — "executed locally or forwarded" — for a
        // command that did neither, so a caller with a working fallback skipped it and a caller
        // without one acked a success.
        TableClaimsAgentFor(_runtime.Options.UniqueNodeId, new Uri("dbcontrol://one"));

        var context = new MessageContext(_runtime);

        var result = await context.InvokeOnAgentOrForwardAsync(
            _agentUri, (_, _) => Task.CompletedTask, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task a_node_that_owns_the_agent_but_has_no_control_endpoint_is_not_an_owner()
    {
        // Guarding the null-forgiving `node!.ControlUri!` this replaced: it would have thrown a
        // NullReferenceException from inside the routing rather than letting the caller fall back.
        TableClaimsAgentFor(Guid.NewGuid(), controlUri: null);

        var context = new MessageContext(_runtime);

        var outcome = await context.InvokeOnAgentAsync(
            _agentUri, (_, _) => Task.CompletedTask, CancellationToken.None);

        outcome.ShouldBe(AgentInvocationOutcome.NoOwner);
    }

    [Fact]
    public async Task running_locally_is_still_reported_as_executed_rather_than_forwarded()
    {
        // The other half of splitting the bool: "the work is done" and "somebody else will do it" are
        // now different answers, so a caller can tell whether to wait for anything.
        _runtime.Agents.AllRunningAgentUris().Returns([_agentUri]);

        var context = new MessageContext(_runtime);
        var actionCalled = false;

        var outcome = await context.InvokeOnAgentAsync(_agentUri, (_, _) =>
        {
            actionCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        outcome.ShouldBe(AgentInvocationOutcome.ExecutedLocally);
        actionCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task no_node_at_all_is_distinct_from_this_node_not_running_it()
    {
        // Two different failures that a single `false` collapsed together, and they want different
        // diagnostics: nobody has been assigned the agent, versus we have been assigned it and are
        // not running it yet.
        _runtime.Agents.AllRunningAgentUris().Returns(Array.Empty<Uri>());
        _runtime.Storage.Nodes.LoadAllNodesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WolverineNode>());

        var context = new MessageContext(_runtime);

        var outcome = await context.InvokeOnAgentAsync(
            _agentUri, (_, _) => Task.CompletedTask, CancellationToken.None);

        outcome.ShouldBe(AgentInvocationOutcome.NoOwner);
    }
}
