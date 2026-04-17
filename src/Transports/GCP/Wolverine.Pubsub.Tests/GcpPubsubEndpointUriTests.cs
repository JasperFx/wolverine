using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class GcpPubsubEndpointUriTests
{
    [Fact]
    public void topic_uri_has_expected_shape()
    {
        GcpPubsubEndpointUri.Topic("my-project", "orders")
            .ShouldBe(new Uri("pubsub://my-project/orders"));
    }

    [Theory]
    [InlineData(null, "orders")]
    [InlineData("", "orders")]
    [InlineData("   ", "orders")]
    [InlineData("proj", null)]
    [InlineData("proj", "")]
    [InlineData("proj", "   ")]
    public void topic_rejects_invalid_args(string? projectId, string? topicName)
    {
        Should.Throw<ArgumentException>(() => GcpPubsubEndpointUri.Topic(projectId!, topicName!));
    }
}
