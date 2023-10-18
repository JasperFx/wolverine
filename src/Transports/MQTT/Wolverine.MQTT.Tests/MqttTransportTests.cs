using Shouldly;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;

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
}