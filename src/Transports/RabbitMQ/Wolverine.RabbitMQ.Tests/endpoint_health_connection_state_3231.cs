using JasperFx.Core;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public record EndpointHealthConnectionStateMessage(string Name);

public class EndpointHealthConnectionStateHandler
{
    public void Handle(EndpointHealthConnectionStateMessage message)
    {
        // no-op; we only need the message to flow so the sender actually connects
    }
}

// GH-3231: EndpointHealthSnapshot must surface the underlying transport channel/agent connection state so external
// monitors (CritterWatch) can see a dead-but-"Accepting" listener (or a disconnected sender) directly rather than
// inferring it from staleness.
[Trait("Category", "Flaky")]
public class endpoint_health_connection_state_3231
{
    private static string nextQueue() => "conn_state_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task healthy_rabbitmq_endpoints_report_connected()
    {
        var queue = nextQueue();
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();
            opts.ListenToRabbitQueue(queue);
            opts.PublishMessage<EndpointHealthConnectionStateMessage>().ToRabbitQueue(queue);
        });

        // Send one message so the (lazily-connecting) sender actually opens its channel before we probe.
        await host.TrackActivity().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new EndpointHealthConnectionStateMessage("Magic Johnson"));

        var snapshots = host.GetRuntime().Endpoints.CollectEndpointHealth();

        var rabbitSnapshots = snapshots.Where(s => s.Uri.Scheme == "rabbitmq").ToList();
        rabbitSnapshots.ShouldNotBeEmpty();

        // Every live RabbitMQ endpoint (listener + sender) should report a healthy, open channel.
        rabbitSnapshots.ShouldContain(s => s.Direction == EndpointDirection.Listening);
        rabbitSnapshots.ShouldContain(s => s.Direction == EndpointDirection.Sending);
        foreach (var snapshot in rabbitSnapshots)
        {
            snapshot.ConnectionState.ShouldBe(TransportConnectionState.Connected,
                $"{snapshot.Direction} endpoint {snapshot.Uri} should report Connected");
        }
    }

    [Fact]
    public async Task a_dropped_sender_connection_is_reported_as_disconnected()
    {
        var queue = nextQueue();
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();
            opts.ListenToRabbitQueue(queue);
            opts.PublishMessage<EndpointHealthConnectionStateMessage>().ToRabbitQueue(queue);
        });

        // Send first so the sender is genuinely Connected before we sever it.
        await host.TrackActivity().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new EndpointHealthConnectionStateMessage("Larry Bird"));

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();

        // Slam the sending connection shut from underneath the transport. The sender heals lazily (only on the
        // next send), so without further traffic it stays Disconnected -- which is exactly the invisible state
        // this issue is about.
        var sending = transport.TryGetSendingConnection();
        if (sending?.Connection is { } sendingConnection)
        {
            await sendingConnection.CloseAsync(
                Constants.ReplySuccess, "Test-induced close", TimeSpan.FromSeconds(5), abort: false, CancellationToken.None);
        }

        // Give the connection-shutdown callback a moment to flip the agent state.
        await Task.Delay(500);

        var snapshots = runtime.Endpoints.CollectEndpointHealth();
        var rabbitSender = snapshots.First(s => s.Direction == EndpointDirection.Sending && s.Uri.Scheme == "rabbitmq");

        rabbitSender.ConnectionState.ShouldBe(TransportConnectionState.Disconnected);
    }
}
