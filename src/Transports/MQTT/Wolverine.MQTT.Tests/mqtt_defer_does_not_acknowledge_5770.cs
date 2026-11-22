using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.MQTT.Tests;

/// <summary>
///     GH-5770. <c>MqttListener.DeferAsync</c> (and its own receive-failure path) used to PUBACK the message and then
///     feed it straight back into the receiver in memory. Once acknowledged the broker never redelivers it, so a
///     re-feed that failed three times, or a process exit, lost it. And a latched receiver defers whatever it is
///     given, so the re-feed deferred again, and again, for as long as the listener object lived.
///
///     <para>Now a deferred message stays unacknowledged until it is handled. A running listener offers it to the
///     receiver again on its own task after a pause; a stopped listener keeps it, unacknowledged, for the listener
///     that replaces it on the same connection. These run without a broker, against an acknowledge handler that
///     counts PUBACKs.</para>
/// </summary>
public class mqtt_defer_does_not_acknowledge_5770 : IAsyncDisposable
{
    private const string TopicName = "defer/5770";

    private readonly TimeSpan _originalDelay = MqttListener.DeferredRedeliveryDelay;
    private readonly MqttTransport _transport = new();
    private readonly IManagedMqttClient _client = Substitute.For<IManagedMqttClient>();
    private readonly MqttTopic _topic;
    private readonly RecordingReceiver _receiver = new();
    private readonly MqttListener _listener;
    private int _acks;

    public mqtt_defer_does_not_acknowledge_5770()
    {
        MqttListener.DeferredRedeliveryDelay = TimeSpan.FromMilliseconds(10);

        _topic = new MqttTopic(TopicName, _transport, EndpointRole.Application);
        _listener = new MqttListener(_transport, NullLogger.Instance, _topic, _receiver, _client);
    }

    public async ValueTask DisposeAsync()
    {
        MqttListener.DeferredRedeliveryDelay = _originalDelay;
        await _listener.DisposeAsync();
    }

    private int Acks => Volatile.Read(ref _acks);

    private MqttEnvelope receivedEnvelope(MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce)
    {
        var message = new MqttApplicationMessage
        {
            Topic = TopicName,
            PayloadSegment = Encoding.UTF8.GetBytes("{}"),
            QualityOfServiceLevel = qos
        };

        var args = new MqttApplicationMessageReceivedEventArgs("client-5770", message,
            new MqttPublishPacket { Topic = TopicName, PacketIdentifier = 1, QualityOfServiceLevel = qos },
            (_, _) =>
            {
                Interlocked.Increment(ref _acks);
                return Task.CompletedTask;
            }) { AutoAcknowledge = false };

        return new MqttEnvelope(_topic, args) { Id = Guid.NewGuid() };
    }

    [Fact]
    public async Task a_deferred_message_is_not_acknowledged_until_it_is_handled()
    {
        var envelope = receivedEnvelope();

        await _listener.DeferAsync(envelope);
        await _receiver.WaitForReceivesAsync(1);

        Acks.ShouldBe(0, "the message was offered again, but nothing has handled it yet");

        // The redelivery is handled
        await _listener.CompleteAsync(envelope);
        await waitForAsync(() => Acks == 1);
    }

    [Fact]
    public async Task a_redelivery_that_keeps_failing_does_not_lose_the_message()
    {
        _receiver.Throws = true;
        var envelope = receivedEnvelope();

        await _listener.DeferAsync(envelope);
        await _receiver.WaitForReceivesAsync(3);

        Acks.ShouldBe(0, "an unacknowledged message is still the broker's to redeliver");
    }

    [Fact]
    public async Task an_envelope_that_keeps_being_deferred_does_not_grow_the_stack()
    {
        _receiver.DefersEverything = true;

        await _listener.DeferAsync(receivedEnvelope());
        await _receiver.WaitForReceivesAsync(10);

        int[] depths;
        lock (_receiver.Depths) depths = _receiver.Depths.ToArray();
        depths.Skip(1).Max().ShouldBeLessThanOrEqualTo(depths[1] + 5);

        Acks.ShouldBe(0);
    }

    [Fact]
    public async Task a_latched_receiver_stops_being_fed_once_the_listener_stops()
    {
        _receiver.DefersEverything = true;

        await _listener.DeferAsync(receivedEnvelope());
        await _receiver.WaitForReceivesAsync(3);

        await _listener.StopAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var receives = _receiver.Receives;

        await Task.Delay(300, TestContext.Current.CancellationToken);
        _receiver.Receives.ShouldBe(receives, "a stopped listener must not keep re-feeding its receiver");
        Acks.ShouldBe(0);
    }

    [Fact]
    public async Task a_stopped_listener_hands_a_deferred_message_back_unacknowledged()
    {
        await _listener.StopAsync();

        await _listener.DeferAsync(receivedEnvelope());
        await Task.Delay(200, TestContext.Current.CancellationToken);

        _receiver.Receives.ShouldBe(0);
        Acks.ShouldBe(0);
    }

    [Fact]
    public async Task the_listener_that_replaces_a_stopped_one_takes_over_its_deferred_messages()
    {
        await _transport.SubscribeToTopicAsync(TopicName, _listener, _topic, _client);
        await _listener.StopAsync();

        var envelope = receivedEnvelope();
        await _listener.DeferAsync(envelope);

        // What a restart after a pause does: build and subscribe a new listener for the same topic
        var nextReceiver = new RecordingReceiver();
        await using var next = new MqttListener(_transport, NullLogger.Instance, _topic, nextReceiver, _client);
        await _transport.SubscribeToTopicAsync(TopicName, next, _topic, _client);

        // New messages go to the new listener, not the stopped one
        _transport.tryFindListener(_client, TopicName, out var found).ShouldBeTrue();
        found.ShouldBeSameAs(next);

        await nextReceiver.WaitForReceivesAsync(1);
        nextReceiver.Received.ShouldContain(envelope);
        Acks.ShouldBe(0);

        await next.CompleteAsync(envelope);
        await waitForAsync(() => Acks == 1);
    }

    [Fact]
    public async Task completing_a_message_twice_acknowledges_it_once()
    {
        // BufferedInMemory acknowledges on receipt, and a deferred message comes back through receipt
        var envelope = receivedEnvelope();

        await _listener.CompleteAsync(envelope);
        await _listener.CompleteAsync(envelope);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Acks.ShouldBe(1);
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "timed out");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public class RecordingReceiver : IReceiver
    {
        private int _receives;

        public bool Throws { get; set; }
        public bool DefersEverything { get; set; }

        public List<int> Depths { get; } = new();
        public List<Envelope> Received { get; } = new();

        public int Receives => Volatile.Read(ref _receives);

        public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
        {
            lock (Depths)
            {
                Depths.Add(new StackTrace().FrameCount);
                Received.Add(envelope);
            }

            Interlocked.Increment(ref _receives);

            if (Throws) throw new DivideByZeroException("The receiver failed");

            // A latched receiver: hand it straight back, from inside this receive
            if (DefersEverything) await listener.DeferAsync(envelope);
        }

        public async Task WaitForReceivesAsync(int count)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (Receives < count)
            {
                DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "timed out waiting for the receiver");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        public ValueTask ReceivedAsync(IListener listener, Envelope[] messages) => ValueTask.CompletedTask;

        public ValueTask DrainAsync() => ValueTask.CompletedTask;

        public IHandlerPipeline Pipeline => null!;

        public void Dispose()
        {
        }
    }
}
