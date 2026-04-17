using Shouldly;
using Xunit;

namespace Wolverine.Nats.Tests;

public class NatsEndpointUriTests
{
    [Fact]
    public void subject_uri_has_expected_shape()
    {
        NatsEndpointUri.Subject("orders.created")
            .ShouldBe(new Uri("nats://subject/orders.created"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void subject_rejects_invalid_name(string? subject)
    {
        Should.Throw<ArgumentException>(() => NatsEndpointUri.Subject(subject!));
    }

    [Fact]
    public void subject_uri_roundtrips_through_parser()
    {
        var uri = NatsEndpointUri.Subject("orders.created");
        var transport = new Wolverine.Nats.Internal.NatsTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }
}
