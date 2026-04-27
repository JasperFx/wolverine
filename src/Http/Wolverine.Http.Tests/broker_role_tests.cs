using Shouldly;
using Wolverine.Configuration;
using Wolverine.Http.Transport;
using Xunit;

namespace Wolverine.Http.Tests;

public class broker_role_tests
{
    [Fact]
    public void http_endpoint_broker_role_is_route()
    {
        new HttpEndpoint(new Uri("http://localhost:5000/orders"), EndpointRole.Application)
            .BrokerRole.ShouldBe("route");
    }
}
