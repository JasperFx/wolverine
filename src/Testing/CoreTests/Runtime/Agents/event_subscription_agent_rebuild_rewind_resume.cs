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
/// Regression coverage for GH-3520. Under Wolverine-managed event-subscription distribution there is no
/// store coordinator to resurrect a shard the daemon stopped. RebuildProjectionAsync stops the continuous
/// agent and never restarts it; RewindSubscriptionAsync restarts it daemon-side but the wrapper still
/// pointed at the stopped pre-rewind agent and reported Running. Either way the shard froze at
/// RegisteredIdle while its high-water climbed. EventSubscriptionAgent.RebuildAsync/RewindAsync must now
/// restore continuous execution themselves through the registered daemon start path and refresh their view.
/// </summary>
public class event_subscription_agent_rebuild_rewind_resume
{
    private readonly IProjectionDaemon _daemon = Substitute.For<IProjectionDaemon>();

    // Store-global shard ("Incident:All", TenantId null) - the shape in the GH-3519/3520 report.
    private readonly ShardName _shardName = new("Incident");
    private readonly JasperFxSubscriptionAgent _restarted = Substitute.For<JasperFxSubscriptionAgent>();
    private readonly EventSubscriptionAgent _agent;

    public event_subscription_agent_rebuild_rewind_resume()
    {
        _daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(_restarted);
        _agent = new EventSubscriptionAgent(
            new Uri("event-subscriptions://marten/incident/all"), _shardName, _daemon);
    }

    [Fact]
    public async Task rebuild_restores_continuous_execution_through_the_registered_start_path()
    {
        await _agent.RebuildAsync(CancellationToken.None);

        // Before the fix RebuildAsync returned without restarting anything: no start call, and the
        // wrapper kept its stale status. Now it resumes through the registered daemon start path.
        await _daemon.Received(1).StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>());
        _agent.Status.ShouldBe(AgentStatus.Running);
    }

    [Fact]
    public async Task rewind_restores_continuous_execution_through_the_registered_start_path()
    {
        await _agent.RewindAsync(0, null, CancellationToken.None);

        await _daemon.Received(1).StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>());
        _agent.Status.ShouldBe(AgentStatus.Running);
    }
}
