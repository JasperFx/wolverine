using Shouldly;
using Wolverine.Oracle.Transport;
using Xunit;

namespace OracleTests.Transport;

/// <summary>
/// GH-3820. Oracle is the only database transport whose <c>SanitizeIdentifier</c> upper-cases
/// (Postgres and SQL Server both lower-case), and it inherited its siblings' <c>findEndpointByUri</c>
/// verbatim: <c>Queues[uri.Host]</c>. <see cref="System.Uri"/> normalises the authority to lower
/// case, so an uppercased queue name never came back out of its own Uri — the lookup silently
/// created a *second* endpoint over the same physical queue tables.
/// </summary>
public class oracle_queue_uri_identity
{
    [Fact]
    public void queue_uri_round_trips_to_the_same_endpoint()
    {
        var transport = new OracleTransport();
        var queue = transport.Queues[transport.MaybeCorrectName("resetone")];

        // Oracle upper-cases identifiers...
        queue.Name.ShouldBe("RESETONE");

        // ...but System.Uri lower-cases the authority, so the Uri cannot carry the casing back.
        queue.Uri.Host.ShouldBe("resetone");

        // Resolving the endpoint from its own Uri must therefore correct the name again, or it
        // hands back a brand new queue pointed at the same tables.
        transport.GetOrCreateEndpoint(queue.Uri).ShouldBeSameAs(queue);
        transport.Queues.Count().ShouldBe(1);
    }

    [Fact]
    public void queue_uri_round_trips_when_the_name_needs_dash_correction()
    {
        var transport = new OracleTransport();
        var queue = transport.Queues[transport.MaybeCorrectName("reset-two")];

        queue.Name.ShouldBe("RESET_TWO");

        transport.GetOrCreateEndpoint(queue.Uri).ShouldBeSameAs(queue);
        transport.Queues.Count().ShouldBe(1);
    }
}
