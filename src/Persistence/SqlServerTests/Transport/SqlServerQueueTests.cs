using IntegrationTests;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Transport;
using Shouldly;

namespace SqlServerTests.Transport;

public class SqlServerQueueTests
{
    private readonly SqlServerTransport theTransport = new SqlServerTransport(new DatabaseSettings
    {
        ConnectionString = Servers.SqlServerConnectionString,
        SchemaName = "transport"
    });
    
    [Fact]
    public void build_uri()
    {
        var queue = new SqlServerQueue("one", theTransport);
        queue.Name.ShouldBe("one");
        queue.Uri.ShouldBe(new Uri("sqlserver://one"));
    }

    [Fact]
    public void must_implement_database_backed_listener()
    {
        var queue = new SqlServerQueue("one", theTransport);
        (queue is IDatabaseBackedEndpoint).ShouldBeTrue();
    }
    
    [Fact]
    public void mode_can_be_durable_or_buffered()
    {
        var queue = new SqlServerQueue("one", theTransport);
        queue.Mode = EndpointMode.Durable;
        queue.Mode = EndpointMode.BufferedInMemory;
    }

    [Fact]
    public void mode_defaults_to_durable()
    {
        var queue = new SqlServerQueue("one", theTransport);
        queue.Mode.ShouldBe(EndpointMode.Durable);
    }

    [Fact]
    public void mode_cannot_be_inline()
    {
        Should.Throw<InvalidOperationException>(() =>
        {
            var queue = new SqlServerQueue("one", theTransport);
            queue.Mode = EndpointMode.Inline;
        });
    }
}