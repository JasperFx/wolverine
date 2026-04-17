using Shouldly;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

public class SnsEndpointUriTests
{
    [Fact]
    public void topic_uri_has_expected_shape()
    {
        SnsEndpointUri.Topic("events")
            .ShouldBe(new Uri("sns://events"));
    }

    [Fact]
    public void topic_uri_preserves_fifo_suffix()
    {
        SnsEndpointUri.Topic("events.fifo")
            .ShouldBe(new Uri("sns://events.fifo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void topic_rejects_invalid_name(string? name)
    {
        Should.Throw<ArgumentException>(() => SnsEndpointUri.Topic(name!));
    }

    [Fact]
    public void topic_uri_roundtrips_through_parser()
    {
        var uri = SnsEndpointUri.Topic("events");
        var transport = new Wolverine.AmazonSns.Internal.AmazonSnsTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }
}
