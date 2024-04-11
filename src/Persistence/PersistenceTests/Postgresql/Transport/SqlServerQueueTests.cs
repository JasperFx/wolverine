using IntegrationTests;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Postgresql.Transport;
using Xunit;

namespace PersistenceTests.Postgresql.Transport;

public class PostgresqlQueueTests
{
    private readonly PostgresqlTransport theTransport = new PostgresqlTransport();
    
    [Fact]
    public void build_uri()
    {
        var queue = new PostgresqlQueue("one", theTransport);
        queue.Name.ShouldBe("one");
        queue.Uri.ShouldBe(new Uri("sqlserver://one"));
    }

    [Fact]
    public void must_implement_database_backed_listener()
    {
        var queue = new PostgresqlQueue("one", theTransport);
        (queue is IDatabaseBackedEndpoint).ShouldBeTrue();
    }
    
    [Fact]
    public void mode_can_be_durable_or_buffered()
    {
        var queue = new PostgresqlQueue("one", theTransport);
        queue.Mode = EndpointMode.Durable;
        queue.Mode = EndpointMode.BufferedInMemory;
    }

    [Fact]
    public void mode_defaults_to_durable()
    {
        var queue = new PostgresqlQueue("one", theTransport);
        queue.Mode.ShouldBe(EndpointMode.Durable);
    }

    [Fact]
    public void mode_cannot_be_inline()
    {
        Should.Throw<InvalidOperationException>(() =>
        {
            var queue = new PostgresqlQueue("one", theTransport);
            queue.Mode = EndpointMode.Inline;
        });
    }
}