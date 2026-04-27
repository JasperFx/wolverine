using Shouldly;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Xunit;

namespace Wolverine.MQTT.Tests;

public class broker_role_tests
{
    [Fact]
    public void mqtt_topic_broker_role_is_topic()
    {
        new MqttTopic("orders/created", new MqttTransport(), EndpointRole.Application)
            .BrokerRole.ShouldBe("topic");
    }
}
