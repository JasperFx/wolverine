using RabbitMQ.Client;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

[Trait("Category", "Flaky")]
public class RabbitMqBrokerHealthProbe_tests
{
    [Fact]
    public async Task probe_before_transport_starts_is_unknown()
    {
        // RabbitMqTransport implements IBrokerHealthProbe directly so unstarted
        // transports can still be probed -- they should report Unknown rather
        // than blow up.
        var transport = new RabbitMqTransport();
        IBrokerHealthProbe probe = transport;

        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        snapshot.TransportType.ShouldBe("RabbitMQ");
        snapshot.Status.ShouldBe(BrokerHealthStatus.Unknown);
        snapshot.ReconnectAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task probe_returns_healthy_for_a_running_transport()
    {
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();
        });

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<RabbitMqTransport>();

        // Transport should be discoverable as a broker health probe via the
        // documented OfType<IBrokerHealthProbe>() extension point.
        var runtime = host.GetRuntime();
        runtime.Options.Transports.OfType<IBrokerHealthProbe>().ShouldContain(transport);

        var snapshot = await ((IBrokerHealthProbe)transport).ProbeAsync(CancellationToken.None);

        snapshot.TransportType.ShouldBe("RabbitMQ");
        snapshot.Status.ShouldBe(BrokerHealthStatus.Healthy);
        snapshot.TransportUri.Scheme.ShouldBe("rabbitmq");
        // Initial connect doesn't count as a reconnect.
        snapshot.ReconnectAttempts.ShouldBe(0);
        snapshot.LastSuccessfulAt.ShouldBeGreaterThan(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task probe_returns_unhealthy_when_underlying_connection_is_closed()
    {
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();
        });

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<RabbitMqTransport>();

        // Slam the listening connection shut from underneath the transport. This
        // simulates a broker-side eviction without going through the host's own
        // shutdown path.
        var listening = transport.TryGetListeningConnection();
        var sending = transport.TryGetSendingConnection();
        if (listening?.Connection is { } listeningConnection)
        {
            await listeningConnection.CloseAsync(
                Constants.ReplySuccess, "Test-induced close", TimeSpan.FromSeconds(5), abort: false, CancellationToken.None);
        }
        if (sending?.Connection is { } sendingConnection)
        {
            await sendingConnection.CloseAsync(
                Constants.ReplySuccess, "Test-induced close", TimeSpan.FromSeconds(5), abort: false, CancellationToken.None);
        }

        // Give the connection-shutdown event a moment to fire on the client.
        await Task.Delay(500);

        var snapshot = await ((IBrokerHealthProbe)transport).ProbeAsync(CancellationToken.None);

        snapshot.Status.ShouldBe(BrokerHealthStatus.Unhealthy);
        snapshot.Description.ShouldNotBeNull();
    }
}
