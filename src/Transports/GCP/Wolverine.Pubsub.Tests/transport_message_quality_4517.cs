using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-4517: the Pub/Sub Uri guard threw ArgumentOutOfRangeException with no message at all, so a
/// typo in a Uri surfaced as "Specified argument was out of the range of valid values. (Parameter 'uri')".
/// </summary>
public class transport_message_quality_4517
{
    [Fact]
    public void uri_with_the_wrong_scheme_names_the_expected_shape()
    {
        var transport = new PubsubTransport();

        var ex = Should.Throw<ArgumentOutOfRangeException>(() =>
            transport.TryGetEndpoint(new Uri("pubsu://wolverine-test/orders")));

        ex.Message.ShouldContain("pubsub://{projectId}/{topicName}");
        ex.Message.ShouldContain("pubsu://wolverine-test/orders");
    }
}
