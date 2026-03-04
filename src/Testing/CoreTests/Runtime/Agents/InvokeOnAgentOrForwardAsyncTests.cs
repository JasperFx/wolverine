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
