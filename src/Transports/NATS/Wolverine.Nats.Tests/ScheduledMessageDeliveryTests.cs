using FluentAssertions;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Reproduces the NATS JetStream native scheduled-delivery failure (err 10190). Unlike the compliance
/// <c>schedule_send</c> test, the publishing endpoint here uses <c>.UseJetStream(stream)</c> — that is what
/// engages the native scheduled-send path; without it Wolverine falls back to durable scheduling and the
/// bug is never exercised.
/// </summary>
[Collection("NATS Integration Tests")]
[Trait("Category", "Integration")]
public class ScheduledMessageDeliveryTests : IAsyncLifetime
{
    private IHost? _sender;
    private IHost? _receiver;
    private string _receiverSubject = "";
    private string _streamName = "";

    public async Task InitializeAsync()
    {
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";

        // Unique per run (GUID, not an in-process counter) so repeated runs against a persistent NATS
        // instance never collide on stream name / subjects. The stream is torn down in DisposeAsync.
        var id = Guid.NewGuid().ToString("N");
        _streamName = $"SCHEDULED_{id}";
        _receiverSubject = $"test.scheduled.{id}.receiver";
        var streamSubjects = $"test.scheduled.{id}.>";

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ScheduleSender";
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .UseJetStream(js => js.MaxDeliver = 5)
                    .DefineWorkQueueStream(_streamName, s => s.EnableScheduledDelivery(), streamSubjects);

                // .UseJetStream on the PUBLISHING endpoint is what forces the native scheduled-send path.
                opts.PublishMessage<ScheduledPing>()
                    .ToNatsSubject(_receiverSubject)
                    .UseJetStream(_streamName);
            })
            .StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ScheduleReceiver";
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .UseJetStream(js => js.MaxDeliver = 5)
                    .DefineWorkQueueStream(_streamName, s => s.EnableScheduledDelivery(), streamSubjects);

                opts.ListenToNatsSubject(_receiverSubject)
                    .Named("receiver")
                    .UseJetStream(_streamName, $"receiver-consumer-{id}");
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        // Delete the run's stream while a connection is still open so persistent NATS instances
        // don't accumulate long-lived test artifacts. Best-effort: the stream may never have been
        // provisioned (e.g. startup failed), so swallow any error.
        if (_sender != null && _streamName.IsNotEmpty())
        {
            try
            {
                var runtime = _sender.Services.GetRequiredService<IWolverineRuntime>();
                var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();
                await transport.JetStreamContext.DeleteStreamAsync(_streamName);
            }
            catch
            {
                // ignore — best-effort cleanup
            }
        }

        if (_sender != null)
        {
            await _sender.StopAsync();
        }

        if (_receiver != null)
        {
            await _receiver.StopAsync();
        }

        _sender?.Dispose();
        _receiver?.Dispose();
    }

    [Fact]
    public async Task schedule_send_over_native_jetstream_scheduling()
    {
        // Native scheduled delivery requires NATS Server 2.12+. Skip on older images rather than fail.
        var runtime = _sender!.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();
        if (!transport.ServerSupportsScheduledSend)
        {
            return;
        }

        // Guard: confirm the native path is engaged, not the durable-scheduling fallback that would mask the bug.
        var agent = runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"nats://subject/{_receiverSubject}"));
        agent.SupportsNativeScheduledSend.Should().BeTrue();

        var message = new ScheduledPing(Guid.NewGuid(), "scheduled");

        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<ScheduledPing>(_receiver!)
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(message, 5.Seconds()).AsTask());

        session.Received.SingleMessage<ScheduledPing>().Should().BeEquivalentTo(message);
    }
}

public record ScheduledPing(Guid Id, string Text);

public class ScheduledPingHandler
{
    public void Handle(ScheduledPing message)
    {
    }
}
