using Dekaf.Errors;
using Dekaf.Protocol;
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

// GH-3454 (split from GH-3237): the Kafka listener derives a degrade-only connection state from the
// exceptions its consume loop observes. The resting state for a healthy listener is Unknown — the
// heuristic may move state toward trouble, but must never synthesize Connected.
public class connection_state_3454
{
    [Fact]
    public void bootstrap_resolution_failure_means_disconnected()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyException(new BootstrapResolutionException(["nosuchhost:9092"], TimeSpan.FromSeconds(5)));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Disconnected);
    }

    [Fact]
    public void a_timeout_means_reconnecting()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyException(new KafkaTimeoutException("timed out waiting on the broker"));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Reconnecting);
    }

    [Fact]
    public void a_retriable_kafka_error_means_reconnecting()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyException(KafkaException.FromErrorCode(ErrorCode.LeaderNotAvailable, "leader moved"));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Reconnecting);
    }

    [Fact]
    public void a_retriable_error_never_downgrades_disconnected()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyException(new BootstrapResolutionException(["nosuchhost:9092"], TimeSpan.FromSeconds(5)));
        tracker.ApplyException(new KafkaTimeoutException("timed out"));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Disconnected);
    }

    [Fact]
    public void message_level_errors_say_nothing_about_the_connection()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyException(KafkaException.FromErrorCode(ErrorCode.OffsetOutOfRange, "bad offset"));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Unknown);
    }

    [Fact]
    public void a_successful_consume_clears_derived_trouble_back_to_unknown()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyException(new BootstrapResolutionException(["nosuchhost:9092"], TimeSpan.FromSeconds(5)));
        tracker.MarkSuccessfulConsume();
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Unknown);
    }

    [Fact]
    public async Task healthy_listener_rests_at_unknown_and_never_reports_connected()
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

        // ...and that a working listener still reports Unknown, never a synthesized Connected
        var snapshot = host.GetRuntime().Endpoints.CollectEndpointHealth()
            .Single(s => s.Direction == EndpointDirection.Listening && s.Uri.Scheme == "kafka");

        snapshot.ConnectionState.ShouldBe(TransportConnectionState.Unknown);
    }

    [Fact]
    public async Task unreachable_broker_degrades_off_unknown()
    {
        // Nothing listens on this port, so the consume loop observes connection trouble.
        // No AutoProvision — admin calls against a dead broker would stall startup
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:19092");
                opts.ListenToKafkaTopic("connstate-dead");
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var state = await ConnectionStateTestHelpers.WaitForListenerConnectionStateAsync(
            host, "kafka", TransportConnectionState.Disconnected, 30000);

        // A dead broker surfaces as either hard Disconnected (resolution failure) or Reconnecting
        // (timeouts the client retries) — either way, never a synthesized Connected
        state.ShouldBeOneOf(TransportConnectionState.Disconnected, TransportConnectionState.Reconnecting);
    }
}

public record ConnStateMessage;

public static class ConnStateMessageHandler
{
    public static void Handle(ConnStateMessage message)
    {
    }
}
