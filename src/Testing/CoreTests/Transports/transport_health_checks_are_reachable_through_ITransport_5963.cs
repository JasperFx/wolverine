using NSubstitute;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

// GH-5963: a transport's BuildHealthCheck was reachable only through its concrete type. Every caller that asks
// through ITransport (Wolverine.HealthChecks, the transport compliance tests, monitoring tools) got the
// interface's default null instead.
public class transport_health_checks_are_reachable_through_ITransport_5963
{
    private readonly IWolverineRuntime theRuntime = Substitute.For<IWolverineRuntime>();

    [Fact]
    public void a_broker_transport_health_check_is_built_through_the_interface()
    {
        ITransport transport = new HealthCheckedBrokerTransport();

        transport.BuildHealthCheck(theRuntime).ShouldBeOfType<StubTransportHealthCheck>();
    }

    [Fact]
    public void a_transport_base_health_check_is_built_through_the_interface()
    {
        ITransport transport = new HealthCheckedTransport();

        transport.BuildHealthCheck(theRuntime).ShouldBeOfType<StubTransportHealthCheck>();
    }

    [Fact]
    public void a_transport_without_a_health_check_still_builds_none()
    {
        ITransport transport = new FakeTransport();

        transport.BuildHealthCheck(theRuntime).ShouldBeNull();
    }

    [Fact]
    public void the_options_transport_collection_finds_the_health_check()
    {
        var options = new WolverineOptions();
        options.Transports.Add(new HealthCheckedBrokerTransport());

        options.Transports
            .Select(x => x.BuildHealthCheck(theRuntime))
            .OfType<StubTransportHealthCheck>()
            .ShouldHaveSingleItem();
    }
}

public class HealthCheckedBrokerTransport : FakeTransport
{
    public override WolverineTransportHealthCheck BuildHealthCheck(IWolverineRuntime runtime)
    {
        return new StubTransportHealthCheck();
    }
}

public class HealthCheckedTransport : TransportBase<FakeEndpoint>
{
    public HealthCheckedTransport() : base("checked", "Checked", ["checked"])
    {
    }

    public override WolverineTransportHealthCheck BuildHealthCheck(IWolverineRuntime runtime)
    {
        return new StubTransportHealthCheck();
    }

    protected override IEnumerable<FakeEndpoint> endpoints()
    {
        return [];
    }

    protected override FakeEndpoint findEndpointByUri(Uri uri)
    {
        throw new NotImplementedException();
    }
}

public class StubTransportHealthCheck : WolverineTransportHealthCheck
{
    public override string TransportName => "Stub";
    public override string Protocol => "stub";

    public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TransportHealthResult(TransportName, Protocol, TransportHealthStatus.Healthy, null,
            DateTimeOffset.UtcNow));
    }
}
