using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents.DynamicListeners;

/// <summary>
/// End-to-end coverage for <see cref="DynamicListenerAgent"/>: spin up a real
/// Wolverine host with the stub transport in solo mode, manually drive the
/// agent's lifecycle, and verify the listener actually goes live and is
/// recognizable to the rest of the runtime via
/// <see cref="IEndpointCollection.FindListeningAgent"/>.
///
/// This is intentionally below the cluster-assignment layer: we don't wait
/// for <see cref="DurabilitySettings.CheckAssignmentPeriod"/> to fire — that
/// path is exercised by the broader cluster compliance tests once a
/// transport-specific dynamic registration test ships in PR-3 (MQTT).
/// </summary>
public class dynamic_listener_agent_lifecycle_integration : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            // Solo mode keeps us out of the cluster orchestration path so the
            // test exercises just the agent — the family registration logic
            // is covered separately by DynamicListenerAgentFamilyTests.
            opts.Durability.Mode = DurabilityMode.Solo;
        });
    }

    public Task DisposeAsync()
    {
        _host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task agent_start_activates_the_listener_on_the_runtime()
    {
        // Arrange: a stub-transport listener URI that the runtime's existing
        // stub transport knows how to materialize into an Endpoint.
        var listenerUri = new Uri("stub://gh-2685-dynamic-target");

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var agent = new DynamicListenerAgent(runtime, listenerUri);

        // Act
        await agent.StartAsync(CancellationToken.None);

        try
        {
            // Assert: the runtime is now listening at the requested URI. This
            // verifies the agent went all the way through transport-resolution
            // → endpoint creation → ListeningAgent registration without us
            // pre-configuring the URI in WolverineOptions.
            runtime.Endpoints.FindListeningAgent(listenerUri).ShouldNotBeNull();
        }
        finally
        {
            await agent.StopAsync(CancellationToken.None);
            agent.Status.ShouldBe(AgentStatus.Stopped);
        }
    }

    [Fact]
    public async Task agent_start_throws_when_no_transport_supports_the_scheme()
    {
        // Misconfiguration scenario: a listener URI was registered for a
        // transport the host doesn't include. Surface it as a hard error at
        // StartAsync rather than letting the listener silently never come up.
        var bogusUri = new Uri("not-a-real-scheme://host/topic");

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var agent = new DynamicListenerAgent(runtime, bogusUri);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            agent.StartAsync(CancellationToken.None));

        ex.Message.ShouldContain("not-a-real-scheme");
        ex.Message.ShouldContain("UseMqtt"); // hint baked into the message
    }

    [Fact]
    public async Task agent_stop_before_start_is_a_no_op()
    {
        // The agent runtime can call StopAsync on an agent that never had a
        // chance to start (e.g. node deactivation between assignment and
        // first poll); tolerate that without throwing.
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var agent = new DynamicListenerAgent(runtime, new Uri("stub://gh-2685-never-started"));

        await Should.NotThrowAsync(() => agent.StopAsync(CancellationToken.None));
        agent.Status.ShouldBe(AgentStatus.Stopped);
    }
}
