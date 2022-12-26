using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals;

public class RabbitMqTopicEndpointTests
{
    [Fact]
    public void topic_name_is_the_endpoint_name()
    {
        var transport = new RabbitMqTransport();
        var exchange = new RabbitMqExchange("bar", transport);

        var endpoint = new RabbitMqTopicEndpoint("important", exchange, transport);

        endpoint.EndpointName.ShouldBe(endpoint.TopicName);
    }

    [Fact]
    public void construct_uri()
    {
        var transport = new RabbitMqTransport();
        var exchange = new RabbitMqExchange("bar", transport);

        var endpoint = new RabbitMqTopicEndpoint("important", exchange, transport);
        endpoint.Uri.ShouldBe(new Uri("rabbitmq://topic/bar/important"));
    }
}