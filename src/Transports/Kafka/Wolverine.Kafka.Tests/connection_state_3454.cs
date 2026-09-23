using Confluent.Kafka;
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

// GH-3454 (split from GH-3237): librdkafka exposes no queryable connection state, so the Kafka listeners
// derive a degrade-only state from the consumer's error callback. The resting state for a healthy listener
// is Unknown — the heuristic may move state toward trouble, but must never synthesize Connected. When user
// configuration already claims SetErrorHandler through ConfigureConsumerBuilders, Wolverine backs off
// instead of throwing on Confluent's double-registration guard.
public class connection_state_3454
{
    [Fact]
    public void fatal_error_means_disconnected()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyError(new Error(ErrorCode.Unknown, "boom", true));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Disconnected);
    }

    [Fact]
    public void all_brokers_down_means_disconnected()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyError(new Error(ErrorCode.Local_AllBrokersDown));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Disconnected);
    }

    [Theory]
    [InlineData(ErrorCode.Local_Transport)]
    [InlineData(ErrorCode.Local_TimedOut)]
    [InlineData(ErrorCode.Local_Resolve)]
    public void broker_transport_trouble_means_reconnecting(ErrorCode code)
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyError(new Error(code));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Reconnecting);
    }

    [Fact]
    public void a_per_broker_error_never_downgrades_all_brokers_down()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyError(new Error(ErrorCode.Local_AllBrokersDown));
        tracker.ApplyError(new Error(ErrorCode.Local_Transport));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Disconnected);
    }

    [Fact]
    public void message_level_errors_say_nothing_about_the_connection()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyError(new Error(ErrorCode.OffsetOutOfRange));
        tracker.ConnectionState.ShouldBe(TransportConnectionState.Unknown);
    }

    [Fact]
    public void a_successful_consume_clears_derived_trouble_back_to_unknown()
    {
        var tracker = new KafkaConnectionStateTracker();
        tracker.ApplyError(new Error(ErrorCode.Local_AllBrokersDown));
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
    public async Task unreachable_broker_degrades_to_disconnected()
    {
        // Nothing listens on this port, so librdkafka raises transport errors and then all-brokers-down.
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

    // GH-4522: this used to be `user_claimed_error_handler_backs_off_instead_of_throwing`, and it asserted
    // that the connection state stayed Unknown -- which was the bug. A user error handler registered through
    // ConfigureConsumerBuilders silently disabled Wolverine's connection-state tracking for the lifetime of
    // the host, so health checks, wolverine-diagnostics and CritterWatch could not tell a healthy consumer
    // from one that had been disconnected for an hour. Wolverine now composes behind the user's handler
    // instead of losing the race to register one, so BOTH run.
    [Fact]
    public async Task a_user_error_handler_and_wolverine_tracking_both_run()
    {
        var userHandlerHits = 0;

        // Nothing listens on this port, so librdkafka raises transport errors and then all-brokers-down.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:19092")
                    .ConfigureConsumerBuilders(b =>
                        b.SetErrorHandler((_, _) => Interlocked.Increment(ref userHandlerHits)));
                opts.ListenToKafkaTopic("connstate-user-handler");
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The user's handler still fires -- their registration is untouched and runs first...
        var state = await ConnectionStateTestHelpers.WaitForListenerConnectionStateAsync(
            host, "kafka", TransportConnectionState.Disconnected, 30000);

        userHandlerHits.ShouldBeGreaterThan(0);

        // ...and Wolverine now sees the same errors, so the state degrades instead of resting at Unknown.
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
