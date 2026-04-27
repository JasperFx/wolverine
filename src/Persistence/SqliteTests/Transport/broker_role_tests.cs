using Shouldly;
using Wolverine.Sqlite.Transport;
using Xunit;

namespace SqliteTests.Transport;

public class broker_role_tests
{
    [Fact]
    public void sqlite_queue_broker_role_is_queue()
    {
        new SqliteQueue("q", new SqliteTransport()).BrokerRole.ShouldBe("queue");
    }
}
