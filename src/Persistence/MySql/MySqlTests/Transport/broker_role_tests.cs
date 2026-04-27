using Shouldly;
using Wolverine.MySql.Transport;
using Xunit;

namespace MySqlTests.Transport;

public class broker_role_tests
{
    [Fact]
    public void mysql_queue_broker_role_is_queue()
    {
        new MySqlQueue("q", new MySqlTransport()).BrokerRole.ShouldBe("queue");
    }
}
