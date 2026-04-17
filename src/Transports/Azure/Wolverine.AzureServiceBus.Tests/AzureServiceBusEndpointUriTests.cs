using Shouldly;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class AzureServiceBusEndpointUriTests
{
    [Fact]
    public void queue_uri_has_expected_shape()
    {
        AzureServiceBusEndpointUri.Queue("orders")
            .ShouldBe(new Uri("asb://queue/orders"));
    }

    [Fact]
    public void topic_uri_has_expected_shape()
    {
        AzureServiceBusEndpointUri.Topic("events")
            .ShouldBe(new Uri("asb://topic/events"));
    }

    [Fact]
    public void subscription_uri_has_expected_shape()
    {
        AzureServiceBusEndpointUri.Subscription("events", "audit")
            .ShouldBe(new Uri("asb://topic/events/audit"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void queue_rejects_invalid_name(string? name)
    {
        Should.Throw<ArgumentException>(() => AzureServiceBusEndpointUri.Queue(name!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void topic_rejects_invalid_name(string? name)
    {
        Should.Throw<ArgumentException>(() => AzureServiceBusEndpointUri.Topic(name!));
    }

    [Theory]
    [InlineData(null, "sub")]
    [InlineData("", "sub")]
    [InlineData("   ", "sub")]
    [InlineData("topic", null)]
    [InlineData("topic", "")]
    [InlineData("topic", "   ")]
    public void subscription_rejects_invalid_args(string? topic, string? sub)
    {
        Should.Throw<ArgumentException>(() => AzureServiceBusEndpointUri.Subscription(topic!, sub!));
    }

    [Fact]
    public void queue_uri_roundtrips_through_parser()
    {
        var uri = AzureServiceBusEndpointUri.Queue("orders");
        var transport = new AzureServiceBusTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }

    [Fact]
    public void topic_uri_roundtrips_through_parser()
    {
        var uri = AzureServiceBusEndpointUri.Topic("events");
        var transport = new AzureServiceBusTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }

    [Fact]
    public void subscription_uri_roundtrips_through_parser()
    {
        var uri = AzureServiceBusEndpointUri.Subscription("events", "audit");
        var transport = new AzureServiceBusTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }
}
