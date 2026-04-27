using Shouldly;
using Wolverine.Postgresql.Transport;
using Xunit;

namespace PostgresqlTests.Transport;

public class broker_role_tests
{
    [Fact]
    public void postgresql_queue_broker_role_is_queue()
    {
        new PostgresqlQueue("q", new PostgresqlTransport()).BrokerRole.ShouldBe("queue");
    }
}
