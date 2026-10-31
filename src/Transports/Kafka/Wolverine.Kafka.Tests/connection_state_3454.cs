using Dekaf.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

// GH-3454 (split from GH-3237): the Kafka listener reports the connection state of its Dekaf consumer's broker
// connections.
public class connection_state_3454
{
    private static KafkaClientStatus statusOf(bool isStopped = false, params BrokerConnectionState[] brokers)
    {
        return new KafkaClientStatus
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Role = KafkaClientRole.Consumer,
            IsStopped = isStopped,
            Brokers = brokers.Select((state, i) => new BrokerConnectionStatus
            {
                BrokerId = i,
                Host = "localhost",
                Port = 9092 + i,
                State = state,
                ConnectionCount = 1,
                ConnectedConnectionCount = state == BrokerConnectionState.Disconnected ? 0 : 1,
                PendingRequestCount = 0
            }).ToList()
        };
    }

    [Fact]
    public void one_live_broker_connection_means_connected()
    {
        KafkaConnectionState.From(statusOf(false, BrokerConnectionState.Disconnected, BrokerConnectionState.Connected))
            .ShouldBe(TransportConnectionState.Connected);
    }

    [Fact]
    public void a_partially_connected_broker_means_connected()
    {
        KafkaConnectionState.From(statusOf(false, BrokerConnectionState.PartiallyConnected))
            .ShouldBe(TransportConnectionState.Connected);
    }

    [Fact]
    public void no_live_broker_connection_means_reconnecting()
    {
        KafkaConnectionState.From(statusOf(false, BrokerConnectionState.Disconnected, BrokerConnectionState.Disconnected))
            .ShouldBe(TransportConnectionState.Reconnecting);
    }

    private static KafkaClientStatus idleStatus(DateTimeOffset? lastSuccess, DateTimeOffset? lastError)
    {
        return new KafkaClientStatus
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Role = KafkaClientRole.Producer,
            Brokers =
            [
                new BrokerConnectionStatus
                {
                    BrokerId = 0,
                    Host = "localhost",
                    Port = 9092,
                    State = BrokerConnectionState.Disconnected,
                    ConnectionCount = 0,
                    ConnectedConnectionCount = 0,
                    PendingRequestCount = 0,
                    LastSuccessfulRequestAtUtc = lastSuccess,
                    LastErrorAtUtc = lastError
                }
            ]
        };
    }

    [Fact]
    public void a_broker_whose_connections_were_closed_for_being_idle_means_connected()
    {
        var now = DateTimeOffset.UtcNow;
        KafkaConnectionState.From(idleStatus(now.AddMinutes(-10), now.AddHours(-1)))
            .ShouldBe(TransportConnectionState.Connected);
    }

    [Fact]
    public void a_broker_that_failed_after_its_last_success_means_reconnecting()
    {
        var now = DateTimeOffset.UtcNow;
        KafkaConnectionState.From(idleStatus(now.AddMinutes(-10), now.AddMinutes(-1)))
            .ShouldBe(TransportConnectionState.Reconnecting);
    }

    [Fact]
    public void a_broker_never_reached_means_reconnecting()
    {
        KafkaConnectionState.From(idleStatus(null, DateTimeOffset.UtcNow))
            .ShouldBe(TransportConnectionState.Reconnecting);
    }

    [Fact]
    public void a_stopped_client_means_disconnected()
    {
        KafkaConnectionState.From(statusOf(true, BrokerConnectionState.Connected))
            .ShouldBe(TransportConnectionState.Disconnected);
    }

    [Fact]
    public async Task healthy_listener_reports_connected()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();

                opts.PublishMessage<ConnStateMessage>().ToKafkaTopic("connstate-3454");
                // BeginAtEarliest so a record produced before the group finishes joining is still consumed
                opts.ListenToKafkaTopic("connstate-3454").BeginAtEarliest();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Prove records actually flow...
        await host.TrackActivity().IncludeExternalTransports().Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<ConnStateMessage>(host)
            .SendMessageAndWaitAsync(new ConnStateMessage());

        // ...and that the working listener reports its live broker connections
        var snapshot = host.GetRuntime().Endpoints.CollectEndpointHealth()
            .Single(s => s.Direction == EndpointDirection.Listening && s.Uri.Scheme == "kafka");

        snapshot.ConnectionState.ShouldBe(TransportConnectionState.Connected);
    }

    [Fact]
    public async Task unreachable_broker_means_disconnected()
    {
        // Nothing listens on this port, so the consumer never initializes.
        // No AutoProvision — admin calls against a dead broker would stall startup
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:19092");
                opts.ListenToKafkaTopic("connstate-dead");
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var state = await ConnectionStateTestHelpers.WaitForListenerConnectionStateAsync(
            host, "kafka", TransportConnectionState.Disconnected, 30000);

        state.ShouldBe(TransportConnectionState.Disconnected);
    }
}

public record ConnStateMessage;

public static class ConnStateMessageHandler
{
    public static void Handle(ConnStateMessage message)
    {
    }
}
