using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

// Locks down GH-2601: every concrete RabbitMQ endpoint type reports the
// expected BrokerRole the CritterWatch UI expects.
public class broker_role_tests
{
    [Fact]
    public void rabbitmq_queue_broker_role_is_queue()
    {
        new RabbitMqQueue("q", new RabbitMqTransport()).BrokerRole.ShouldBe("queue");
    }

    [Fact]
    public void rabbitmq_exchange_broker_role_is_exchange()
    {
        new RabbitMqExchange("ex", new RabbitMqTransport()).BrokerRole.ShouldBe("exchange");
    }

    [Fact]
    public void rabbitmq_topic_endpoint_broker_role_is_topic()
    {
        var transport = new RabbitMqTransport();
        var exchange = new RabbitMqExchange("ex", transport);
        new RabbitMqTopicEndpoint("t", exchange, transport).BrokerRole.ShouldBe("topic");
    }

    [Fact]
    public void rabbitmq_routing_broker_role_is_exchange()
    {
        var transport = new RabbitMqTransport();
        var exchange = new RabbitMqExchange("ex", transport);
        new RabbitMqRouting(exchange, "rk", transport).BrokerRole.ShouldBe("exchange");
    }
}
