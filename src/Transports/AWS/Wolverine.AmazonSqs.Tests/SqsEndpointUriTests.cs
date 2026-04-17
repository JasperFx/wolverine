using Shouldly;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

public class SqsEndpointUriTests
{
    [Fact]
    public void queue_uri_has_expected_shape()
    {
        SqsEndpointUri.Queue("orders")
            .ShouldBe(new Uri("sqs://orders"));
    }

    [Fact]
    public void queue_uri_preserves_fifo_suffix()
    {
        SqsEndpointUri.Queue("orders.fifo")
            .ShouldBe(new Uri("sqs://orders.fifo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void queue_rejects_invalid_name(string? name)
    {
        Should.Throw<ArgumentException>(() => SqsEndpointUri.Queue(name!));
    }

    [Fact]
    public void queue_uri_roundtrips_through_parser()
    {
        var uri = SqsEndpointUri.Queue("orders");
        var transport = new Wolverine.AmazonSqs.Internal.AmazonSqsTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }
}
