using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Transport;
using Xunit;

namespace PersistenceTests.Bugs;

// The database-backed control transport is the inter-node command channel for
// Wolverine's leader election + agent reassignment workflow. Marking its endpoint
// as Durable would push every control envelope through the durable inbox/outbox,
// which both deadlocks the durability layer (it depends on the same store) and
// produces nonsensical outbox rows for transient cluster-coordination messages.
// Marking it Inline is similarly wrong — control messages are batched + polled.
//
// These tests pin down the contract: SupportsMode returns true ONLY for
// BufferedInMemory, and any direct attempt to set Mode to anything else throws.
// Built-in policies (UseDurableInboxOnAllListeners, etc.) already guard their
// setters with SupportsMode checks and will silently skip; explicit programmer
// errors via custom IEndpointPolicy.Apply will surface as a clear exception.
public class database_control_endpoint_mode_tests
{
    private static DatabaseControlEndpoint endpoint()
    {
        // The transport instance is held only for its TableName / Database refs at
        // listen/send time; SupportsMode is a pure property of the endpoint itself.
        var database = Substitute.For<IMessageDatabase>();
        database.SchemaName.Returns("registry");
        var transport = new DatabaseControlTransport(database, new WolverineOptions());
        return (DatabaseControlEndpoint)transport.ControlEndpoint;
    }

    [Fact]
    public void supports_only_buffered_in_memory_mode()
    {
        var e = endpoint();

        e.SupportsMode(EndpointMode.BufferedInMemory).ShouldBeTrue();
        e.SupportsMode(EndpointMode.Durable).ShouldBeFalse();
        e.SupportsMode(EndpointMode.Inline).ShouldBeFalse();
    }

    [Fact]
    public void setting_mode_to_durable_throws()
    {
        var e = endpoint();
        Should.Throw<InvalidOperationException>(() => e.Mode = EndpointMode.Durable);
    }

    [Fact]
    public void setting_mode_to_inline_throws()
    {
        var e = endpoint();
        Should.Throw<InvalidOperationException>(() => e.Mode = EndpointMode.Inline);
    }

    [Fact]
    public void setting_mode_to_buffered_in_memory_succeeds()
    {
        var e = endpoint();

        // Idempotent — already BufferedInMemory by default; this just exercises the
        // happy path of the setter so a future regression that flips the predicate
        // backwards (allow nothing) is caught loudly.
        e.Mode = EndpointMode.BufferedInMemory;

        e.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}
