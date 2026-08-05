using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// GH-3832 — a deliberate pause must report the distinct <see cref="ListeningStatus.Paused"/>,
/// never the self-recovering <see cref="ListeningStatus.TooBusy"/> (and never linger as a bare
/// Stopped). Before this, a guard written against "paused" had nothing to check for, and
/// monitoring products could not tell operator intent from transient back pressure.
/// </summary>
public class PausedListeningStatusTests : IAsyncLifetime
{
    private readonly int _port;
    private IHost _host = null!;

    public PausedListeningStatusTests()
    {
        _port = PortFinder.GetAvailablePort();
    }

    public async ValueTask InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.ListenAtPort(_port).Named("paused-status-test");
            opts.LocalQueue("paused-status-queue").UseDurableInbox();
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task pausing_a_listening_agent_reports_paused_and_start_resumes_accepting()
    {
        var runtime = _host.GetRuntime();
        var uri = $"tcp://localhost:{_port}".ToUri();

        var agent = runtime.Endpoints.FindListeningAgent(uri);
        agent.ShouldNotBeNull();
        agent.Status.ShouldBe(ListeningStatus.Accepting);

        await agent.PauseAsync(5.Minutes());

        agent.Status.ShouldBe(ListeningStatus.Paused);

        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
    }

    [Fact]
    public async Task pausing_a_durable_local_queue_reports_paused_not_too_busy()
    {
        var runtime = _host.GetRuntime();

        var circuit = runtime.Endpoints.AgentForLocalQueue("local://paused-status-queue".ToUri())
            .ShouldBeAssignableTo<IListenerCircuit>();
        circuit.ShouldNotBeNull();
        circuit.Status.ShouldBe(ListeningStatus.Accepting);

        await circuit.PauseAsync(5.Minutes());

        // The whole point of GH-3832: an administrative pause is not the self-recovering
        // back-pressure latch, and must not read as one.
        circuit.Status.ShouldBe(ListeningStatus.Paused);

        await circuit.StartAsync();

        circuit.Status.ShouldBe(ListeningStatus.Accepting);
    }
}
