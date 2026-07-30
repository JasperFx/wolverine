using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Runtime.Agents;

// GH-3604: ExclusiveListenerFamily / LeaderPinnedListenerFamily build their agent set once in the
// constructor and hand the SAME instance back from BuildAgentAsync for the life of the runtime. So a
// listener that fails over away from a node and later returns to it is restarted on the very object
// the earlier StopAsync() marked Stopped. StartAsync() has to put the status back, or that node runs
// the listener while reporting it as not running: it never reappears in AllRunningAgentUris(), and
// CheckHealthAsync() reports Unhealthy forever.
//
// The observable symptom was multi_node_exclusive_listener_failover.listener_fails_over_when_the_
// leader_running_it_crashes timing out after 90s with "it kept flapping" -- misleading, since the
// failover itself worked and only the reporting was stuck.
public class listener_agent_status_resets_on_restart
{
    private static Endpoint EndpointFor(string scheme)
        => new SchemeEndpoint(new Uri($"{scheme}://queue/incoming")) { EndpointName = "incoming" };

    private static IWolverineRuntime RuntimeThatStartsAndStopsListeners()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Endpoints.Returns(Substitute.For<IEndpointCollection>());

        return runtime;
    }

    [Fact]
    public async Task exclusive_listener_reports_running_again_after_a_restart()
    {
        var agent = new ExclusiveListenerAgent(EndpointFor("rabbitmq"), RuntimeThatStartsAndStopsListeners());

        await agent.StopAsync(TestContext.Current.CancellationToken);
        agent.Status.ShouldBe(AgentStatus.Stopped);

        await agent.StartAsync(TestContext.Current.CancellationToken);
        agent.Status.ShouldBe(AgentStatus.Running);
    }

    [Fact]
    public async Task leader_pinned_listener_reports_running_again_after_a_restart()
    {
        // This one moves on every leader election, so a stuck status is guaranteed to bite the second
        // time the role comes back to a node.
        var agent = new LeaderPinnedListenerAgent(EndpointFor("rabbitmq"), RuntimeThatStartsAndStopsListeners());

        await agent.StopAsync(TestContext.Current.CancellationToken);
        agent.Status.ShouldBe(AgentStatus.Stopped);

        await agent.StartAsync(TestContext.Current.CancellationToken);
        agent.Status.ShouldBe(AgentStatus.Running);
    }

    [Fact]
    public async Task a_restarted_exclusive_listener_is_healthy_again()
    {
        // CheckHealthAsync short-circuits to Unhealthy on any non-Running status before it ever looks at
        // the real listening agent, so a stuck status is what monitoring sees.
        var agent = new ExclusiveListenerAgent(EndpointFor("rabbitmq"), RuntimeThatStartsAndStopsListeners());

        await agent.StopAsync(TestContext.Current.CancellationToken);
        (await agent.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status.ShouldBe(HealthStatus.Unhealthy);

        await agent.StartAsync(TestContext.Current.CancellationToken);
        (await agent.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status.ShouldBe(HealthStatus.Healthy);
    }

    // Minimal concrete Endpoint whose Uri scheme is controllable — the only thing the listener agent
    // ctors read besides EndpointName.
    private sealed class SchemeEndpoint : Endpoint
    {
        public SchemeEndpoint(Uri uri) : base(uri, EndpointRole.Application)
        {
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime)
            => throw new NotSupportedException();
    }
}
