using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace OracleTests.Transport;

// Mirror of the shared DatabaseControlEndpoint mode lock — Oracle has its own
// control transport (oraclecontrol://) introduced in #2622 and must enforce the
// same invariant: the control endpoint can ONLY ever be BufferedInMemory.
// Marking it Durable would push every leader-election / agent-reassignment
// envelope through the durable inbox/outbox (which sits on top of the same
// Oracle store, leading to deadlocks); marking it Inline contradicts the
// transport's batched-poll semantics. Built-in policies already guard their
// setters with SupportsMode checks; this test pins down that direct setter
// abuse fails fast.
public class oracle_control_endpoint_mode_tests
{
    private static OracleControlEndpoint endpoint()
    {
        // No live connection is needed for endpoint construction — OracleMessageStore's
        // ctor only inspects connection-string parts via OracleConnectionStringBuilder.
        var dataSource = new OracleDataSource(
            "User Id=mode_test;Password=x;Data Source=localhost:1521/FREEPDB1");
        var settings = new DatabaseSettings
        {
            SchemaName = "MODE_TEST",
            Role = MessageStoreRole.Main
        };
        var store = new OracleMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<OracleMessageStore>.Instance);
        var transport = new OracleControlTransport(store, new WolverineOptions());
        return transport.ControlEndpoint;
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
        e.Mode = EndpointMode.BufferedInMemory;
        e.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}
