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

namespace Wolverine.MQTT.Tests;

/// <summary>
///     GH-5790. A message that reaches a connection before its listener registers is held, unacknowledged, and
///     handed to the listener when it registers, instead of being left in flight for a reconnect that may never
///     come. No broker: the transport's receive path is driven directly, against an acknowledge handler that
///     counts PUBACKs.
/// </summary>
public class mqtt_backlog_held_until_listener_5790
{
    private const string TopicName = "backlog/5790";

    private readonly MqttTransport _transport = new();
    private readonly IManagedMqttClient _client = Substitute.For<IManagedMqttClient>();
    private readonly MqttTopic _topic;
    private readonly mqtt_defer_does_not_acknowledge_5770.RecordingReceiver _receiver = new();
    private readonly MqttListener _listener;
    private int _acks;

    public mqtt_backlog_held_until_listener_5790()
    {
        _topic = new MqttTopic(TopicName, _transport, EndpointRole.Application);
        _listener = new MqttListener(_transport, NullLogger.Instance, _topic, _receiver, _client);
    }

    private int Acks => Volatile.Read(ref _acks);

    private MqttApplicationMessageReceivedEventArgs arrival(string topic = TopicName)
    {
        var message = new MqttApplicationMessage
        {
            Topic = topic,
            PayloadSegment = Encoding.UTF8.GetBytes("{}"),
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
        };

        return new MqttApplicationMessageReceivedEventArgs("client-5790", message,
            new MqttPublishPacket { Topic = topic, PacketIdentifier = 1, QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce },
            (_, _) =>
            {
                Interlocked.Increment(ref _acks);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task a_message_that_arrives_before_its_listener_is_handed_to_it_when_it_registers()
    {
        var early = arrival();
        await _transport.ReceiveFromClientAsync(_client, early);

        early.AutoAcknowledge.ShouldBeFalse("MQTTnet must not acknowledge it when the handler returns");
        _receiver.Receives.ShouldBe(0);

        await _transport.SubscribeToTopicAsync(TopicName, _listener, _topic, _client);

        await _receiver.WaitForReceivesAsync(1);
        Acks.ShouldBe(0, "handed over, not acknowledged; the listener acknowledges once it is handled");
    }

    [Fact]
    public async Task held_messages_are_handed_over_in_arrival_order()
    {
        var arrivals = Enumerable.Range(0, 5).Select(_ => arrival()).ToArray();
        foreach (var args in arrivals)
        {
            await _transport.ReceiveFromClientAsync(_client, args);
        }

        await _transport.SubscribeToTopicAsync(TopicName, _listener, _topic, _client);

        await _receiver.WaitForReceivesAsync(5);
        lock (_receiver.Depths)
        {
            _receiver.Received.Cast<MqttEnvelope>().Select(x => x.Args).ShouldBe(arrivals);
        }
    }

    [Fact]
    public async Task a_wildcard_listener_takes_the_messages_its_filter_matches()
    {
        var wildcard = new MqttTopic("backlog/#", _transport, EndpointRole.Application);
        var listener = new MqttListener(_transport, NullLogger.Instance, wildcard, _receiver, _client);

        await _transport.ReceiveFromClientAsync(_client, arrival("backlog/5790/a"));
        await _transport.ReceiveFromClientAsync(_client, arrival("elsewhere/5790"));

        await _transport.SubscribeToTopicAsync("backlog/#", listener, wildcard, _client);

        await _receiver.WaitForReceivesAsync(1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _receiver.Receives.ShouldBe(1, "only the matching message is this listener's");
    }

    [Fact]
    public async Task past_the_bound_a_message_is_left_unacknowledged_and_not_held()
    {
        _transport.MaximumHeldBeforeListeners = 2;

        for (var i = 0; i < 3; i++)
        {
            await _transport.ReceiveFromClientAsync(_client, arrival());
        }

        await _transport.SubscribeToTopicAsync(TopicName, _listener, _topic, _client);

        await _receiver.WaitForReceivesAsync(2);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // The third stays in flight on the broker until the session resumes on a new connection
        _receiver.Receives.ShouldBe(2);
        Acks.ShouldBe(0);
    }

    [Fact]
    public async Task once_startup_completes_a_held_message_nothing_listens_to_is_discarded()
    {
        await _transport.ReceiveFromClientAsync(_client, arrival("nobody/5790"));

        _transport.MarkListenersAreUp();

        // Acknowledged so it stops holding an in-flight slot, as a post-startup message for that topic would be
        await waitForAsync(() => Acks == 1);
        _receiver.Receives.ShouldBe(0);
    }

    [Fact]
    public async Task a_message_arriving_after_startup_for_no_listener_is_still_discarded_without_holding()
    {
        _transport.MarkListenersAreUp();

        var late = arrival("nobody/5790");
        await _transport.ReceiveFromClientAsync(_client, late);

        // Unchanged GH-4802 behaviour: left to MQTTnet's automatic acknowledgement
        late.AutoAcknowledge.ShouldBeTrue();

        await _transport.SubscribeToTopicAsync("nobody/5790",
            new MqttListener(_transport, NullLogger.Instance,
                new MqttTopic("nobody/5790", _transport, EndpointRole.Application), _receiver, _client),
            _topic, _client);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        _receiver.Receives.ShouldBe(0);
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
}
