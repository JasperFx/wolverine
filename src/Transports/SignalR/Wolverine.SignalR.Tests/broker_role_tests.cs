using Shouldly;
using Wolverine.SignalR.Client;
using Wolverine.SignalR.Internals;
using Xunit;

namespace Wolverine.SignalR.Tests;

public class broker_role_tests
{
    [Fact]
    public void signalr_transport_broker_role_is_hub()
    {
        new SignalRTransport().BrokerRole.ShouldBe("hub");
    }

    [Fact]
    public void signalr_client_endpoint_broker_role_is_hub()
    {
        var clientTransport = new SignalRClientTransport();
        new SignalRClientEndpoint(new Uri("https://localhost:5000/hub"), clientTransport)
            .BrokerRole.ShouldBe("hub");
    }
}
