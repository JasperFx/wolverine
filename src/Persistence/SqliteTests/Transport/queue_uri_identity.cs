using JasperFx.Core;
using Shouldly;
using Wolverine.Sqlite.Transport;
using Xunit;

namespace SqliteTests.Transport;

// A queue reached only by Uri bypasses the fluent API's MaybeCorrectName, and the raw
// dashed name reached the wolverine_queue_* table DDL as an invalid identifier. Same
// correction as the SQL Server / Postgresql / Oracle (GH-3820) transports.
public class queue_uri_identity
{
    [Fact]
    public void queue_name_from_uri_is_sanitized()
    {
        var transport = new SqliteTransport();
        var queue = transport.GetOrCreateEndpoint("sqlite://my-service-control".ToUri());
        queue.ShouldBeOfType<SqliteQueue>().Name.ShouldBe("my_service_control");
    }

    [Fact]
    public void dashed_uri_and_fluent_name_resolve_to_the_same_endpoint()
    {
        var transport = new SqliteTransport();
        var queue = transport.Queues[transport.MaybeCorrectName("my-service-control")];
        transport.GetOrCreateEndpoint("sqlite://my-service-control".ToUri()).ShouldBeSameAs(queue);
        transport.GetOrCreateEndpoint(queue.Uri).ShouldBeSameAs(queue);
        transport.Queues.Count().ShouldBe(1);
    }
}
