using Shouldly;
using Wolverine.Configuration;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Configuration;

public class ServerlessEndpointsMustBeInlinePolicy5030Tests
{
    [Fact]
    public void LeavesUnsupportedSystemEndpointsInTheirConfiguredMode()
    {
        var endpoint = new TestEndpoint(EndpointRole.System)
        {
            SupportsInlineListeners = false,
            Mode = EndpointMode.BufferedInMemory
        };

        new ServerlessEndpointsMustBeInlinePolicy().Apply(endpoint, null!);

        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void StillRejectsApplicationEndpointsThatDoNotSupportInline()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            new ServerlessEndpointsMustBeInlinePolicy().Apply(new TcpEndpoint(), null!));

        exception.Message.ShouldContain("All endpoints must be Inline");
    }
}
