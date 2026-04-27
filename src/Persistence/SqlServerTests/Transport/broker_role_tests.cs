using IntegrationTests;
using Shouldly;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Transport;
using Xunit;

namespace SqlServerTests.Transport;

public class broker_role_tests
{
    [Fact]
    public void sql_server_queue_broker_role_is_queue()
    {
        var transport = new SqlServerTransport(new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString,
            SchemaName = "transport"
        });
        new SqlServerQueue("q", transport).BrokerRole.ShouldBe("queue");
    }
}
