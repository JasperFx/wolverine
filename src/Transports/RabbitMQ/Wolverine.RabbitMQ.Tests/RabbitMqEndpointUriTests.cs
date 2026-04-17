using Shouldly;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class RabbitMqEndpointUriTests
{
    [Fact]
    public void queue_uri_has_expected_shape()
    {
        RabbitMqEndpointUri.Queue("orders")
            .ShouldBe(new Uri("rabbitmq://queue/orders"));
    }

    [Fact]
    public void exchange_uri_has_expected_shape()
    {
        RabbitMqEndpointUri.Exchange("events")
            .ShouldBe(new Uri("rabbitmq://exchange/events"));
    }

    [Fact]
    public void topic_uri_has_expected_shape()
    {
        RabbitMqEndpointUri.Topic("prices", "usd.eur")
            .ShouldBe(new Uri("rabbitmq://topic/prices/usd.eur"));
    }

    [Fact]
    public void routing_uri_has_expected_shape()
    {
        RabbitMqEndpointUri.Routing("events", "order.created")
            .ShouldBe(new Uri("rabbitmq://exchange/events/routing/order.created"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void queue_rejects_invalid_name(string? name)
    {
        Should.Throw<ArgumentException>(() => RabbitMqEndpointUri.Queue(name!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void exchange_rejects_invalid_name(string? name)
    {
        Should.Throw<ArgumentException>(() => RabbitMqEndpointUri.Exchange(name!));
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("", "key")]
    [InlineData("   ", "key")]
    [InlineData("ex", null)]
    [InlineData("ex", "")]
    [InlineData("ex", "   ")]
    public void topic_rejects_invalid_args(string? exchange, string? key)
    {
        Should.Throw<ArgumentException>(() => RabbitMqEndpointUri.Topic(exchange!, key!));
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("", "key")]
    [InlineData("   ", "key")]
    [InlineData("ex", null)]
    [InlineData("ex", "")]
    [InlineData("ex", "   ")]
    public void routing_rejects_invalid_args(string? exchange, string? key)
    {
        Should.Throw<ArgumentException>(() => RabbitMqEndpointUri.Routing(exchange!, key!));
    }

    [Fact]
    public void queue_uri_roundtrips_through_parser()
    {
        var uri = RabbitMqEndpointUri.Queue("orders");
        var transport = new Wolverine.RabbitMQ.Internal.RabbitMqTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }

    [Fact]
    public void exchange_uri_roundtrips_through_parser()
    {
        var uri = RabbitMqEndpointUri.Exchange("events");
        var transport = new Wolverine.RabbitMQ.Internal.RabbitMqTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }

    [Fact]
    public void topic_uri_roundtrips_through_parser()
    {
        var uri = RabbitMqEndpointUri.Topic("prices", "usd.eur");
        var transport = new Wolverine.RabbitMQ.Internal.RabbitMqTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }

    [Fact]
    public void routing_uri_roundtrips_through_parser()
    {
        var uri = RabbitMqEndpointUri.Routing("events", "order.created");
        var transport = new Wolverine.RabbitMQ.Internal.RabbitMqTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }
}
