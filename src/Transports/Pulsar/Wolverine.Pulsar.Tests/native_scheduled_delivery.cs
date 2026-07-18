using System.Collections.Concurrent;
using DotPulsar;
using DotPulsar.Extensions;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3470: broker-native delayed delivery for scheduled messages via Pulsar's DeliverAt metadata.

// ---- metadata stamping unit tests (no broker) ----

public class native_scheduled_delivery_metadata
{
    [Fact]
    public void stamps_deliver_at_from_the_envelope_scheduled_time()
    {
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var envelope = new Envelope { ScheduledTime = scheduledTime };
        var outgoing = new MessageMetadata();

        PulsarSender.ApplyScheduledDelivery(envelope, outgoing);

        outgoing.DeliverAtTime.ShouldBe(scheduledTime.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void converts_local_scheduled_times_to_universal_time()
    {
        var localTime = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.FromHours(5));
        var envelope = new Envelope { ScheduledTime = localTime };
        var outgoing = new MessageMetadata();

        PulsarSender.ApplyScheduledDelivery(envelope, outgoing);

        outgoing.DeliverAtTimeAsDateTimeOffset.ShouldBe(localTime.ToUniversalTime());
    }

    [Fact]
    public void leaves_the_metadata_untouched_without_a_scheduled_time()
    {
        var envelope = new Envelope();
        var outgoing = new MessageMetadata();

        PulsarSender.ApplyScheduledDelivery(envelope, outgoing);

        outgoing.DeliverAtTime.ShouldBe(0);
    }

    [Fact]
    public void respects_deliver_at_stamped_by_a_custom_interop_mapper()
    {
        var mapperStamped = DateTimeOffset.UtcNow.AddHours(1);
        var envelope = new Envelope { ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5) };
        var outgoing = new MessageMetadata { DeliverAtTimeAsDateTimeOffset = mapperStamped };

        PulsarSender.ApplyScheduledDelivery(envelope, outgoing);

        outgoing.DeliverAtTime.ShouldBe(mapperStamped.ToUnixTimeMilliseconds());
    }
}

// ---- configuration unit tests (no broker) ----

public class native_scheduled_delivery_configuration
{
    [Fact]
    public void native_scheduled_send_is_enabled_by_default()
    {
        new PulsarTransport().NativeScheduledSendEnabled.ShouldBeTrue();
    }

    [Fact]
    public void disable_native_scheduled_send_flips_the_transport_flag()
    {
        var options = new WolverineOptions();
        var configuration = options.UsePulsar().DisableNativeScheduledSend();

        configuration.Transport.NativeScheduledSendEnabled.ShouldBeFalse();
    }

    [Fact]
    public void broker_per_tenant_transports_inherit_the_flag()
    {
        var options = new WolverineOptions();
        var configuration = options.UsePulsar()
            .DisableNativeScheduledSend()
            .AddTenant("tenant-a", new Uri("pulsar://tenant-a:6650"));

        var transport = configuration.Transport;
        var tenant = transport.Tenants["tenant-a"];
        tenant.Compile(transport);

        tenant.Transport.NativeScheduledSendEnabled.ShouldBeFalse();
    }
}

// ---- broker-native hold proof (Pulsar docker) ----

public class native_scheduled_delivery_broker_hold
{
    // Proves the broker itself holds a scheduled message until its deliver-at time by consuming with
    // a RAW DotPulsar Shared consumer — no Wolverine listener, so Wolverine's receiver-side
    // scheduled-time fallback cannot mask a missing DeliverAt stamp.
    [Fact]
    public async Task broker_holds_the_scheduled_message_until_deliver_at()
    {
        var topic = $"persistent://public/default/native-schedule-{Guid.NewGuid():N}";

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishAllMessages().ToPulsarTopic(topic).SendInline();
            opts.Discovery.DisableConventionalDiscovery();
        });

        await using var client = PulsarClient.Builder()
            .ServiceUrl(PulsarContainerFixture.ServiceUrl)
            .Build();

        await using var consumer = client.NewConsumer()
            .Topic(topic)
            .SubscriptionName("raw-native-schedule")
            .SubscriptionType(SubscriptionType.Shared)
            .Create();

        // Make sure the Shared subscription exists before publishing
        using (var connecting = new CancellationTokenSource(30.Seconds()))
        {
            await consumer.StateChangedTo(ConsumerState.Active, connecting.Token);
        }

        var delay = 6.Seconds();
        var sentAt = DateTimeOffset.UtcNow;
        await publisher.MessageBus().ScheduleAsync(new NativeScheduledPing(Guid.NewGuid()), delay);

        // The message must NOT be visible in the early window — the broker is holding it
        var receivedEarly = false;
        using (var early = new CancellationTokenSource(3.Seconds()))
        {
            try
            {
                await consumer.Receive(early.Token);
                receivedEarly = true;
            }
            catch (OperationCanceledException)
            {
                // expected — nothing deliverable yet
            }
        }

        receivedEarly.ShouldBeFalse();

        // And it MUST arrive once the deliver-at time has passed
        using var late = new CancellationTokenSource(30.Seconds());
        var message = await consumer.Receive(late.Token);
        await consumer.Acknowledge(message, late.Token);

        var receivedAt = DateTimeOffset.UtcNow;

        // One second of tolerance for clock skew between the broker container and the test process
        (receivedAt - sentAt).ShouldBeGreaterThanOrEqualTo(delay - 1.Seconds());
    }
}

// ---- end to end over Wolverine hosts (Pulsar docker) ----

public class native_scheduled_delivery_end_to_end : IAsyncLifetime
{
    private readonly string _topic = $"persistent://public/default/native-schedule-e2e-{Guid.NewGuid():N}";
    private IHost? _sender;
    private IHost? _receiver;

    public async Task InitializeAsync()
    {
        _sender = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishMessage<NativeScheduledPing>().ToPulsarTopic(_topic).SendInline();
            opts.Discovery.DisableConventionalDiscovery();
        });

        _receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));

            // Native delayed delivery is only honored by the broker for Shared / KeyShared subscriptions
            opts.ListenToPulsarTopic(_topic)
                .SubscriptionName("native-scheduled-e2e")
                .SubscriptionType(SubscriptionType.Shared);

            opts.Services.AddSingleton<NativeScheduledSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<NativeScheduledPingHandler>();
        });

        // Give the durable subscription a moment to attach before anything is published
        await Task.Delay(2.Seconds());
    }

    public async Task DisposeAsync()
    {
        if (_sender != null)
        {
            await _sender.StopAsync();
            _sender.Dispose();
        }

        if (_receiver != null)
        {
            await _receiver.StopAsync();
            _receiver.Dispose();
        }
    }

    [Fact]
    public async Task scheduled_message_is_handled_after_its_deliver_at_time_and_plain_sends_are_unaffected()
    {
        // Guard: the native path must be engaged, not the durable-scheduling fallback
        var runtime = _sender!.Services.GetRequiredService<IWolverineRuntime>();
        var agent = runtime.Endpoints.GetOrBuildSendingAgent(PulsarEndpointUri.Topic(_topic));
        agent.SupportsNativeScheduledSend.ShouldBeTrue();

        var sink = _receiver!.Services.GetRequiredService<NativeScheduledSink>();

        // 1) A scheduled message is not handled before its deliver-at time
        var delay = 5.Seconds();
        var scheduled = new NativeScheduledPing(Guid.NewGuid());
        var sentAt = DateTimeOffset.UtcNow;

        await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<NativeScheduledPing>(_receiver!)
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(scheduled, delay));

        var handledAt = sink.HandledAt[scheduled.Id];

        // One second of tolerance for clock skew between the broker container and the test process
        (handledAt - sentAt).ShouldBeGreaterThanOrEqualTo(delay - 1.Seconds());

        // 2) A plain send on the same endpoint is delivered promptly
        var plain = new NativeScheduledPing(Guid.NewGuid());
        var plainSentAt = DateTimeOffset.UtcNow;

        await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<NativeScheduledPing>(_receiver!)
            .ExecuteAndWaitAsync(c => c.SendAsync(plain).AsTask());

        var plainHandledAt = sink.HandledAt[plain.Id];
        (plainHandledAt - plainSentAt).ShouldBeLessThan(10.Seconds());
    }
}

public record NativeScheduledPing(Guid Id);

public class NativeScheduledSink
{
    public ConcurrentDictionary<Guid, DateTimeOffset> HandledAt { get; } = new();
}

public class NativeScheduledPingHandler
{
    public static void Handle(NativeScheduledPing message, NativeScheduledSink sink)
    {
        sink.HandledAt[message.Id] = DateTimeOffset.UtcNow;
    }
}
