using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports;

public class GloballyLatchedListenerTests : IDisposable
{
    private readonly int _port;
    private readonly IHost _host;

    public GloballyLatchedListenerTests()
    {
        _port = PortFinder.GetAvailablePort();

        _host = WolverineHost.For(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.ListenAtPort(_port).Named("latching-test");
        });
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    [Fact]
    public async Task latch_permanently_after_startup()
    {
        var runtime = _host.GetRuntime();
        var uri = $"tcp://localhost:{_port}".ToUri();

        var agent = runtime.Endpoints.FindListeningAgent(uri);
        agent.ShouldNotBeNull();
        agent.Status.ShouldBe(ListeningStatus.Accepting);

        await agent.LatchPermanently();

        agent.Status.ShouldBe(ListeningStatus.GloballyLatched);
    }

    [Fact]
    public async Task latch_permanently_stops_the_listener()
    {
        var runtime = _host.GetRuntime();
        var agent = runtime.Endpoints.FindListeningAgent("latching-test") as ListeningAgent;
        agent.ShouldNotBeNull();
        agent.Listener.ShouldNotBeNull();

        await agent.LatchPermanently();

        agent.Listener.ShouldBeNull();
        agent.Status.ShouldBe(ListeningStatus.GloballyLatched);
    }

    [Fact]
    public async Task stop_and_drain_is_noop_when_globally_latched()
    {
        var runtime = _host.GetRuntime();
        var agent = runtime.Endpoints.FindListeningAgent("latching-test");
        agent.ShouldNotBeNull();

        await agent.LatchPermanently();
        agent.Status.ShouldBe(ListeningStatus.GloballyLatched);

        // StopAndDrainAsync should be a no-op and not change the status
        await agent.StopAndDrainAsync();
        agent.Status.ShouldBe(ListeningStatus.GloballyLatched);
    }

    [Fact]
    public async Task existing_restriction_prevents_listener_start()
    {
        var runtime = _host.GetRuntime();
        var uri = $"tcp://localhost:{_port}".ToUri();

        var agent = runtime.Endpoints.FindListeningAgent(uri);
        agent.ShouldNotBeNull();

        // Stop the listener first
        await agent.StopAndDrainAsync();
        agent.Status.ShouldBe(ListeningStatus.Stopped);

        // Set up a paused restriction matching the endpoint URI
        // and switch to Balanced mode so the restriction check kicks in
        runtime.Restrictions.PauseAgent(uri);
        runtime.DurabilitySettings.Mode = DurabilityMode.Balanced;

        // Now try to start — it should detect the restriction and set GloballyLatched
        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.GloballyLatched);
    }

    [Fact]
    public async Task restriction_check_does_not_apply_in_solo_mode()
    {
        var runtime = _host.GetRuntime();
        var uri = $"tcp://localhost:{_port}".ToUri();

        var agent = runtime.Endpoints.FindListeningAgent(uri);
        agent.ShouldNotBeNull();

        // Stop the listener
        await agent.StopAndDrainAsync();
        agent.Status.ShouldBe(ListeningStatus.Stopped);

        // Add a restriction but stay in Solo mode
        runtime.Restrictions.PauseAgent(uri);
        // Mode is already Solo from constructor

        // Start should succeed normally in Solo mode despite the restriction
        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
    }

    [Fact]
    public async Task can_restart_after_restriction_is_removed()
    {
        var runtime = _host.GetRuntime();
        var uri = $"tcp://localhost:{_port}".ToUri();

        var agent = runtime.Endpoints.FindListeningAgent(uri);
        agent.ShouldNotBeNull();

        // Stop the listener and add restriction in Balanced mode
        await agent.StopAndDrainAsync();
        runtime.Restrictions.PauseAgent(uri);
        runtime.DurabilitySettings.Mode = DurabilityMode.Balanced;

        await agent.StartAsync();
        agent.Status.ShouldBe(ListeningStatus.GloballyLatched);

        // Now remove the restriction and restart
        runtime.Restrictions.RestartAgent(uri);

        await agent.StartAsync();
        agent.Status.ShouldBe(ListeningStatus.Accepting);
    }
}
