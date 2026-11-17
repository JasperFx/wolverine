using Dekaf.Diagnostics;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

// The health check reads the status of the Kafka clients that are already running instead of probing the broker,
// and reports consumer lag as the broker queue depth of a listening endpoint.
public class kafka_health_check
{
    private readonly KafkaTransport theTransport = new();

    private sealed class FakeClient : IKafkaClientHealthSource
    {
        public KafkaClientStatus? Status { get; init; }
        public bool FailedToConnect { get; init; }
    }

    private static KafkaClientStatus statusOf(BrokerConnectionState state, bool isStopped = false,
        bool producerFull = false)
    {
        return new KafkaClientStatus
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Role = producerFull ? KafkaClientRole.Producer : KafkaClientRole.Consumer,
            IsStopped = isStopped,
            Brokers =
            [
                new BrokerConnectionStatus
                {
                    BrokerId = 1,
                    Host = "localhost",
                    Port = 9092,
                    State = state,
                    ConnectionCount = 1,
                    ConnectedConnectionCount = state == BrokerConnectionState.Disconnected ? 0 : 1,
                    PendingRequestCount = 0
                }
            ],
            Producer = producerFull ? new ProducerBacklogStatus(100, 100, 0, 1, 1, 1) : null
        };
    }

    private void add(KafkaClientStatus? status, bool failedToConnect = false)
    {
        theTransport.TrackClient(new FakeClient { Status = status, FailedToConnect = failedToConnect });
    }

    private Task<TransportHealthResult> check()
    {
        return new KafkaHealthCheck(theTransport).CheckHealthAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task connected_clients_are_healthy()
    {
        add(statusOf(BrokerConnectionState.Connected));
        add(statusOf(BrokerConnectionState.PartiallyConnected));

        var result = await check();

        result.Status.ShouldBe(TransportHealthStatus.Healthy);
        result.Data!["ConnectedClients"].ShouldBe(2);
    }

    [Fact]
    public async Task one_client_without_a_connection_is_degraded()
    {
        add(statusOf(BrokerConnectionState.Connected));
        add(statusOf(BrokerConnectionState.Disconnected));

        var result = await check();

        result.Status.ShouldBe(TransportHealthStatus.Degraded);
        result.Data!["DisconnectedClients"].ShouldBe(1);
    }

    [Fact]
    public async Task no_client_with_a_connection_is_unhealthy()
    {
        add(statusOf(BrokerConnectionState.Disconnected));
        add(null, failedToConnect: true);

        var result = await check();

        result.Status.ShouldBe(TransportHealthStatus.Unhealthy);
        result.Data!["DisconnectedClients"].ShouldBe(2);
    }

    [Fact]
    public async Task stopped_and_unconnected_clients_are_ignored()
    {
        add(statusOf(BrokerConnectionState.Connected));
        add(statusOf(BrokerConnectionState.Disconnected, isStopped: true));
        add(null);

        var result = await check();

        result.Status.ShouldBe(TransportHealthStatus.Healthy);
        result.Data!["ConnectedClients"].ShouldBe(1);
        result.Data!["DisconnectedClients"].ShouldBe(0);
    }

    [Fact]
    public async Task a_full_producer_buffer_is_degraded()
    {
        add(statusOf(BrokerConnectionState.Connected, producerFull: true));

        var result = await check();

        result.Status.ShouldBe(TransportHealthStatus.Degraded);
        result.Data!["FullProducerBuffers"].ShouldBe(1);
    }

    [Fact]
    public async Task running_host_is_healthy_and_reports_listener_lag()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();

                opts.PublishMessage<HealthCheckMessage>().ToKafkaTopic("health-check-lag");
                opts.ListenToKafkaTopic("health-check-lag").BeginAtEarliest();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await host.TrackActivity().IncludeExternalTransports().Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<HealthCheckMessage>(host)
            .SendMessageAndWaitAsync(new HealthCheckMessage());

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<KafkaTransport>();
        var healthCheck = transport.BuildHealthCheck(runtime);

        var result = await healthCheck.CheckHealthAsync(TestContext.Current.CancellationToken);
        result.Status.ShouldBe(TransportHealthStatus.Healthy);
        ((int)result.Data!["ConnectedClients"]).ShouldBeGreaterThanOrEqualTo(2); // the listener and the sender

        var lag = await healthCheck.GetBrokerQueueDepthAsync(new Uri("kafka://topic/health-check-lag"),
            TestContext.Current.CancellationToken);
        lag.ShouldNotBeNull();
        lag.Value.ShouldBeGreaterThanOrEqualTo(0);
    }
}

public record HealthCheckMessage;

public static class HealthCheckMessageHandler
{
    public static void Handle(HealthCheckMessage message)
    {
    }
}
