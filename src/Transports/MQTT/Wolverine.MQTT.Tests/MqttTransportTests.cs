using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet.Extensions.ManagedClient;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Wolverine.Transports;

namespace Wolverine.MQTT.Tests;

public class MqttTransportTests
{
    [Theory]
    [InlineData("mqtt://topic/one", "one")]
    [InlineData("mqtt://topic/one/two", "one/two")]
    [InlineData("mqtt://topic/one/two/", "one/two")]
    [InlineData("mqtt://topic/one/two/three", "one/two/three")]
    public void get_topic_name_from_uri(string uriString, string expected)
    {
        MqttTransport.TopicForUri(new Uri(uriString))
            .ShouldBe(expected);
    }

    [Fact]
    public void build_uri_for_endpoint()
    {
        var transport = new MqttTransport();
        new MqttTopic("one/two", transport, EndpointRole.Application)
            .Uri.ShouldBe(new Uri("mqtt://topic/one/two"));
    }

    [Fact]
    public void endpoint_name_is_topic_name()
    {
        var transport = new MqttTransport();
        new MqttTopic("one/two", transport, EndpointRole.Application)
            .EndpointName.ShouldBe("one/two");
    }

    [Fact]
    public void retain_is_false_by_default()
    {
        var transport = new MqttTransport();
        new MqttTopic("one/two", transport, EndpointRole.Application)
            .Retain.ShouldBeFalse();
    }

    // GH-4802. A persistent session means the broker holds this client's subscriptions and starts
    // flushing their backlog the moment the connection comes up -- which is before
    // Endpoints.StartListenersAsync() has called SubscribeToTopicAsync for each topic. Resolving a
    // listener in that window has to fail *transiently*. It used to memoize the failure, so one
    // backlog message arriving early poisoned that topic for the life of the process: every later
    // message resolved to the cached null and was discarded with nothing but an information log.
    [Fact]
    public async Task a_listener_resolution_miss_is_not_memoized_4802()
    {
        var transport = new MqttTransport();
        var client = Substitute.For<IManagedMqttClient>();
        var topic = new MqttTopic("one/two", transport, EndpointRole.Application);

        // The startup window
        transport.tryFindListener(client, "one/two", out _).ShouldBeFalse();

        var listener = new MqttListener(transport, NullLogger.Instance, topic,
            Substitute.For<IReceiver>(), client);
        await transport.SubscribeToTopicAsync("one/two", listener, topic, client);

        transport.tryFindListener(client, "one/two", out var found).ShouldBeTrue();
        found.ShouldBeSameAs(listener);
    }

    // The hit is still memoized -- that is the whole point of the cache, and the receive path asks
    // for it on every message.
    [Fact]
    public async Task a_listener_resolution_hit_is_still_memoized()
    {
        var transport = new MqttTransport();
        var client = Substitute.For<IManagedMqttClient>();
        var topic = new MqttTopic("one/two", transport, EndpointRole.Application);

        var listener = new MqttListener(transport, NullLogger.Instance, topic,
            Substitute.For<IReceiver>(), client);
        await transport.SubscribeToTopicAsync("one/two", listener, topic, client);

        transport.tryFindListener(client, "one/two", out var first).ShouldBeTrue();
        transport.tryFindListener(client, "one/two", out var second).ShouldBeTrue();

        second.ShouldBeSameAs(first);
    }
}
