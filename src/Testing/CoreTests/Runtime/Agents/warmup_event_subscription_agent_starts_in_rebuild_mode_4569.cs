using JasperFx;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;
using IProjectionDaemon = JasperFx.Events.Daemon.IProjectionDaemon;
using JasperFxSubscriptionAgent = JasperFx.Events.Daemon.ISubscriptionAgent;

namespace CoreTests.Runtime.Agents;

/// <summary>
///     GH-4569. On a warm-up node every start of a projection shard -- the first one, the resume after a rebuild or a
///     rewind -- goes through <see cref="IProjectionDaemon.StartAgentInRebuildModeAsync" />, so the new release's
///     projections publish no side effects and resume from their own progress across restarts. A serving node keeps
///     starting continuous.
/// </summary>
public class warmup_event_subscription_agent_starts_in_rebuild_mode_4569
{
    private readonly IProjectionDaemon _daemon = Substitute.For<IProjectionDaemon>();
    private readonly ShardName _shardName = new("Incident");
    private readonly JasperFxSubscriptionAgent _inner = Substitute.For<JasperFxSubscriptionAgent>();

    public warmup_event_subscription_agent_starts_in_rebuild_mode_4569()
    {
        _daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(_inner);
        _daemon.StartAgentInRebuildModeAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(_inner);
    }

    private EventSubscriptionAgent agent(bool warmup) =>
        new(new Uri("event-subscriptions://marten/incident/all"), _shardName, _daemon) { StartsInRebuildMode = warmup };

    [Fact]
    public async Task a_warmup_node_starts_the_shard_in_rebuild_mode()
    {
        var subject = agent(warmup: true);

        await subject.StartAsync(CancellationToken.None);

        await _daemon.Received(1).StartAgentInRebuildModeAsync(_shardName, Arg.Any<CancellationToken>());
        await _daemon.DidNotReceive().StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>());
        subject.Status.ShouldBe(AgentStatus.Running);
    }

    [Fact]
    public async Task a_warmup_node_resumes_in_rebuild_mode_after_a_rebuild()
    {
        await agent(warmup: true).RebuildAsync(CancellationToken.None);

        await _daemon.Received(1).StartAgentInRebuildModeAsync(_shardName, Arg.Any<CancellationToken>());
        await _daemon.DidNotReceive().StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_serving_node_starts_the_shard_continuous()
    {
        await agent(warmup: false).StartAsync(CancellationToken.None);

        await _daemon.Received(1).StartAgentAsync(_shardName, Arg.Any<CancellationToken>());
        await _daemon.DidNotReceive().StartAgentInRebuildModeAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>());
    }
}
