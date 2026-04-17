using Shouldly;
using Xunit;

namespace Wolverine.MQTT.Tests;

public class MqttEndpointUriTests
{
    [Fact]
    public void topic_uri_has_expected_shape()
    {
        MqttEndpointUri.Topic("sensor/temperature")
            .ShouldBe(new Uri("mqtt://topic/sensor/temperature"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void topic_rejects_invalid_name(string? name)
    {
        Should.Throw<ArgumentException>(() => MqttEndpointUri.Topic(name!));
    }

    [Fact]
    public void topic_uri_roundtrips_through_parser()
    {
        var uri = MqttEndpointUri.Topic("sensor/temperature");
        var transport = new Wolverine.MQTT.Internals.MqttTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }
}
