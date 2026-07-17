using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pubsub.Tests;

// GH-3237: the Pub/Sub SDK hides the streaming-pull connection, so PubsubListener derives a degrade-only
// state from its own retry loop. The resting state for a healthy listener is Unknown — the heuristic may
// move the state toward trouble (Reconnecting/Disconnected), but must never synthesize Connected.
public class connection_state_3237
{
    [Fact]
    public async Task healthy_listener_rests_at_unknown_and_never_reports_connected()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<ConnStateMessage>().ToPubsubTopic("connstate");
                opts.ListenToPubsubTopic("connstate");
            }).StartAsync();

        // Prove the pipe actually works...
        await host.TrackActivity().IncludeExternalTransports().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new ConnStateMessage());

        // ...and that a working listener still reports Unknown, never a synthesized Connected
        var snapshot = host.GetRuntime().Endpoints.CollectEndpointHealth()
            .Single(s => s.Direction == EndpointDirection.Listening && s.Uri.Scheme == PubsubTransport.ProtocolName);

        snapshot.ConnectionState.ShouldBe(TransportConnectionState.Unknown);
    }

    [Fact]
    public async Task unreachable_broker_degrades_to_disconnected_after_retries_are_exhausted()
    {
        var original = Environment.GetEnvironmentVariable("PUBSUB_EMULATOR_HOST");

        // Point this host at a port nothing listens on so the streaming pull genuinely fails,
        // driving the retry loop: Reconnecting during backoff, Disconnected once retries run out
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "localhost:18085");

        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // No AutoProvision here — provisioning against the dead endpoint would fail startup
                    opts.UsePubsubTesting();

                    opts.ListenToPubsubTopic("connstate-dead")
                        .ConfigureListener(c =>
                        {
                            c.RetryPolicy.MaxRetryCount = 2;
                            c.RetryPolicy.RetryDelay = 50;
                        });
                }).StartAsync();

            var state = await ConnectionStateTestHelpers.WaitForListenerConnectionStateAsync(
                host, PubsubTransport.ProtocolName, TransportConnectionState.Disconnected);

            state.ShouldBe(TransportConnectionState.Disconnected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", original);
        }
    }
}

public record ConnStateMessage;

public static class ConnStateMessageHandler
{
    public static void Handle(ConnStateMessage message)
    {
    }
}
