using Shouldly;
using Xunit;

namespace Wolverine.Kafka.Tests;

public class KafkaEndpointUriTests
{
    [Fact]
    public void topic_uri_has_expected_shape()
    {
        KafkaEndpointUri.Topic("orders")
            .ShouldBe(new Uri("kafka://topic/orders"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void topic_rejects_invalid_name(string? name)
    {
        Should.Throw<ArgumentException>(() => KafkaEndpointUri.Topic(name!));
    }

    [Fact]
    public void topic_uri_roundtrips_through_parser()
    {
        var uri = KafkaEndpointUri.Topic("orders");
        var transport = new Wolverine.Kafka.Internals.KafkaTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }
}
