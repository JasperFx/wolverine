using Shouldly;
using Wolverine.Oracle.Transport;
using Xunit;

namespace OracleTests.Transport;

public class broker_role_tests
{
    [Fact]
    public void oracle_queue_broker_role_is_queue()
    {
        new OracleQueue("q", new OracleTransport()).BrokerRole.ShouldBe("queue");
    }
}
