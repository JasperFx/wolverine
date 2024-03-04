using IntegrationTests;
using JasperFx.Core;
using Shouldly;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Transport;

namespace SqlServerTests.Transport;

public class SqlServerTransportTests
{
    private readonly SqlServerTransport theTransport = new SqlServerTransport(new DatabaseSettings
    {
        ConnectionString = Servers.SqlServerConnectionString,
        SchemaName = "transport"
    });

    [Fact]
    public void retrieve_queue_by_uri()
    {
        var queue = theTransport.GetOrCreateEndpoint("sqlserver://one".ToUri());
        queue.ShouldBeOfType<SqlServerQueue>().Name.ShouldBe("one");
    }
}