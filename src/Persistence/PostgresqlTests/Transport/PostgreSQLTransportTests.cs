using IntegrationTests;
using JasperFx.Core;
using Shouldly;
using Wolverine.Postgresql.Transport;
using Wolverine.RDBMS;

namespace PostgresqlTests.Transport;

public class PostgresqlTransportTests
{
    private readonly PostgresqlTransport theTransport = new PostgresqlTransport();

    [Fact]
    public void retrieve_queue_by_uri()
    {
        var queue = theTransport.GetOrCreateEndpoint("Postgresql://one".ToUri());
        queue.ShouldBeOfType<PostgresqlQueue>().Name.ShouldBe("one");
    }
}