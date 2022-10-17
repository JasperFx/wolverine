using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs.Tests.Internal;

public class AmazonSqsEndpointTests
{
    [Fact]
    public void default_mode_is_buffered()
    {
        new AmazonSqsEndpoint("foo",new AmazonSqsTransport())
            .Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}