using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Xunit;

namespace Wolverine.Kafka.Tests;

public class broker_role_tests
{
    [Fact]
    public void kafka_topic_broker_role_is_topic()
    {
        new KafkaTopic(new KafkaTransport(), "t", EndpointRole.Application).BrokerRole.ShouldBe("topic");
    }
}
