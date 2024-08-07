using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Transports.Tcp;

public class TcpEndpointTests
{
    [Fact]
    public void default_host()
    {
        new TcpEndpoint()
            .HostName.ShouldBe("localhost");

        new TcpEndpoint(3333)
            .HostName.ShouldBe("localhost");
    }

    [Fact]
    public void default_role_is_application()
    {
        new TcpEndpoint().Role.ShouldBe(EndpointRole.Application);
    }

    [Fact]
    public void will_not_allow_you_to_configure_as_inline()
    {
        Exception<InvalidOperationException>.ShouldBeThrownBy(() => { new TcpEndpoint().Mode = EndpointMode.Inline; });
    }

    [Theory]
    [InlineData(EndpointMode.BufferedInMemory, true)]
    [InlineData(EndpointMode.Durable, true)]
    public void should_enforce_back_pressure(EndpointMode mode, bool shouldEnforce)
    {
        var endpoint = new TcpEndpoint();
        endpoint.Mode = mode;
        endpoint.ShouldEnforceBackPressure().ShouldBe(shouldEnforce);
    }
}