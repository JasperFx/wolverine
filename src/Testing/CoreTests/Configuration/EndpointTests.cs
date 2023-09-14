using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Configuration;

public class EndpointTests
{
    [Fact]
    public void telemetry_is_enabled_by_default()
    {
        new TestEndpoint(EndpointRole.System).TelemetryEnabled.ShouldBeTrue();
        new TestEndpoint(EndpointRole.Application).TelemetryEnabled.ShouldBeTrue();
    }
}

public class TestEndpoint : Endpoint
{
    public TestEndpoint(EndpointRole role) : base(new Uri("stub://one"), role)
    {
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }
}