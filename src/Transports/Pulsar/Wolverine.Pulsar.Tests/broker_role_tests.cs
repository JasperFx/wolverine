using Shouldly;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class broker_role_tests
{
    [Fact]
    public void pulsar_endpoint_broker_role_is_topic()
    {
        var transport = new PulsarTransport();
        new PulsarEndpoint(new Uri("pulsar://persistent/public/default/sample"), transport)
            .BrokerRole.ShouldBe("topic");
    }
}
