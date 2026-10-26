using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// GH-5213. A circuit breaker can pause a listener several times for one trip: failures that were still in flight
/// when the listener stopped are reported afterwards, and each report pauses again. Every pause used to install a new
/// Restarter without disposing the previous one, so all of them fired when their time was up. Since the breaker
/// drains before it pauses (6.13.0), those pauses land together, so the restarts raced in StartAsync: each built a
/// full set of listeners and only the last set was kept. The others stayed attached to the broker as consumers nothing
/// would ever stop, feeding a receiver that a later pause drained.
///
/// A later pause has to supersede the earlier one's restart, the same way PauseForInboxRecoveryAsync already does.
/// </summary>
public class a_later_pause_supersedes_an_earlier_pause_5213 : IAsyncLifetime
{
    private readonly int _port;
    private IHost _host = null!;

    public a_later_pause_supersedes_an_earlier_pause_5213()
    {
        _port = PortFinder.GetAvailablePort();
    }

    public async ValueTask InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.ListenAtPort(_port).Named("superseded-pause-test");
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
    public async Task the_earlier_pauses_restart_does_not_restart_the_listener()
    {
        var agent = _host.GetRuntime().Endpoints.FindListeningAgent($"tcp://localhost:{_port}".ToUri());
        agent.ShouldNotBeNull();

        await agent.PauseAsync(1.Seconds());
        await agent.PauseAsync(5.Minutes());

        // Well past the first pause. Only the second pause's restart is still scheduled.
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        agent.Status.ShouldBe(ListeningStatus.Paused);
    }
}
