using JasperFx.Core;
using Shouldly;
using Wolverine.Postgresql.Transport;

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

    // A queue reached only by Uri (ListenForMessagesFrom("postgresql://my-service-control"))
    // bypasses the fluent API's MaybeCorrectName, and the raw dashed name reached the
    // wolverine_queue_* table DDL as an invalid, unquoted identifier.
    [Fact]
    public void queue_name_from_uri_is_sanitized()
    {
        var queue = theTransport.GetOrCreateEndpoint("postgresql://my-service-control".ToUri());
        queue.ShouldBeOfType<PostgresqlQueue>().Name.ShouldBe("my_service_control");
    }

    [Fact]
    public void dashed_uri_and_fluent_name_resolve_to_the_same_endpoint()
    {
        var queue = theTransport.Queues[theTransport.MaybeCorrectName("my-service-control")];
        theTransport.GetOrCreateEndpoint("postgresql://my-service-control".ToUri()).ShouldBeSameAs(queue);
        theTransport.GetOrCreateEndpoint(queue.Uri).ShouldBeSameAs(queue);
        theTransport.Queues.Count().ShouldBe(1);
    }
}