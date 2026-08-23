using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Transports.Local;
using Wolverine.Transports.Stub;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-3708. EndpointMode.NativeAck is opt-in per transport. These tests exist because the obvious way to add a
/// fourth mode -- routing it through supportsMode() -- is default-open, and several overrides would have accepted
/// it silently on transports whose settlement model cannot express out-of-order completion.
/// </summary>
public class native_ack_mode_gate
{
    /// <summary>An endpoint type that has opted in, standing in for RabbitMQ until it does.</summary>
    private class NativeAckCapableEndpoint : StubEndpoint
    {
        public NativeAckCapableEndpoint(string queueName, StubTransport transport) : base(queueName, transport)
        {
        }

        protected override bool supportsNativeAck => true;
    }

    [Fact]
    public void no_endpoint_type_accepts_native_ack_by_default()
    {
        var endpoint = new StubEndpoint("one", new StubTransport());

        endpoint.SupportsMode(EndpointMode.NativeAck).ShouldBeFalse();

        var ex = Should.Throw<InvalidOperationException>(() => endpoint.Mode = EndpointMode.NativeAck);
        ex.Message.ShouldContain("does not support EndpointMode.NativeAck");
        ex.Message.ShouldContain("supportsNativeAck");

        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void an_endpoint_that_opts_in_accepts_native_ack()
    {
        var endpoint = new NativeAckCapableEndpoint("two", new StubTransport());

        endpoint.SupportsMode(EndpointMode.NativeAck).ShouldBeTrue();

        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
    }

    /// <summary>
    /// The whole reason NativeAck is gated on its own predicate. Both of these overrides are written as negations,
    /// so a fourth enum member routed through supportsMode() would have satisfied them.
    /// </summary>
    [Fact]
    public void a_supportsMode_override_written_as_a_negation_does_not_leak_native_ack()
    {
        // TcpEndpoint.supportsMode is "mode != EndpointMode.Inline"
        var tcp = new TcpEndpoint("localhost", 5099);

        tcp.SupportsMode(EndpointMode.Durable).ShouldBeTrue();
        tcp.SupportsMode(EndpointMode.NativeAck).ShouldBeFalse();

        Should.Throw<InvalidOperationException>(() => tcp.Mode = EndpointMode.NativeAck);
    }

    [Fact]
    public void a_local_queue_never_accepts_native_ack()
    {
        using var host = Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ListenForMessagesFrom("local://nativeack");
        }).Build();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var queue = options.Transports.AllEndpoints().OfType<LocalQueue>().First();

        queue.SupportsMode(EndpointMode.NativeAck).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => queue.Mode = EndpointMode.NativeAck);
    }

    [Fact]
    public void native_ack_does_not_enforce_back_pressure()
    {
        var endpoint = new NativeAckCapableEndpoint("three", new StubTransport());

        endpoint.ShouldEnforceBackPressure().ShouldBeTrue();

        endpoint.Mode = EndpointMode.NativeAck;

        // Broker prefetch is what bounds a native-ack endpoint, so no BackPressureAgent -- same as Inline
        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();
    }

    [Fact]
    public void native_ack_still_reports_its_parallelism_in_diagnostics()
    {
        var endpoint = new NativeAckCapableEndpoint("four", new StubTransport());
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.MaxDegreeOfParallelism = 12;

        // Unlike Inline, this mode DOES read MaxDegreeOfParallelism -- it sizes the execution block. GH-3712.
        endpoint.ModeIgnoresParallelism.ShouldBeFalse();
        endpoint.DescribeMaxDegreeOfParallelism().ShouldBe("12");
    }

    [Fact]
    public void a_global_partitioned_topology_rejects_native_ack()
    {
        var topology = new GlobalPartitionedMessageTopology(new WolverineOptions());

        var nativeAck = Should.Throw<ArgumentOutOfRangeException>(() => topology.Mode(EndpointMode.NativeAck));
        var inline = Should.Throw<ArgumentOutOfRangeException>(() => topology.Mode(EndpointMode.Inline));

        nativeAck.ParamName.ShouldBe("mode");

        // Assert on the MODE NAME, not on the prose. The original version of this test asserted the phrase
        // "companion local queue", which appears in the Inline rejection as well -- so it could not tell the two
        // branches apart and would have passed even if NativeAck fell through to the Inline guard. Requiring the
        // two messages to differ is what actually pins "this mode has its own rejection", and it survives any
        // rewording of either one.
        nativeAck.Message.ShouldContain(nameof(EndpointMode.NativeAck));
        nativeAck.Message.ShouldNotBe(inline.Message);
    }

    [Fact]
    public void the_listener_config_validator_leaves_native_ack_alone()
    {
        var endpoint = new NativeAckCapableEndpoint("five", new StubTransport())
        {
            IsListener = true,
            GroupShardingSlotNumber = PartitionSlots.Five
        };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.MaxDegreeOfParallelism = 10;

        // Partitioned processing plus real parallelism is the POINT of this mode, so the GH-3712 checks -- which
        // reject exactly that combination on an Inline endpoint -- must not fire here.
        ListenerConfigurationValidator.Validate(endpoint).ShouldBeEmpty();
    }
}
