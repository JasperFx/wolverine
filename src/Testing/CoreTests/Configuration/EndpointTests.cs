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

    [Fact]
    public void listener_scope_is_competing_by_default()
    {
        new TestEndpoint(EndpointRole.System)
            .ListenerScope.ShouldBe(ListenerScope.CompetingConsumers);
    }

    [Theory]
    [InlineData(true, ListenerScope.CompetingConsumers, DurabilityMode.Solo, true)]
    [InlineData(false, ListenerScope.CompetingConsumers, DurabilityMode.Solo, false)]
    [InlineData(true, ListenerScope.CompetingConsumers, DurabilityMode.Balanced, true)]
    [InlineData(true, ListenerScope.Exclusive, DurabilityMode.Balanced, false)]
    [InlineData(true, ListenerScope.Exclusive, DurabilityMode.Solo, true)]
    [InlineData(true, ListenerScope.Exclusive, DurabilityMode.Serverless, false)]
    [InlineData(true, ListenerScope.Exclusive, DurabilityMode.MediatorOnly, false)]
    [InlineData(false, ListenerScope.CompetingConsumers, DurabilityMode.Balanced, false)]
    public void should_auto_start_as_listener(bool isListener, ListenerScope scope, DurabilityMode mode, bool shouldStart)
    {
        var endpoint = new TestEndpoint(EndpointRole.System){IsListener = isListener, ListenerScope = scope};
        var settings = new DurabilitySettings { Mode = mode };
        
        endpoint.ShouldAutoStartAsListener(settings).ShouldBe(shouldStart);

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

    public bool SupportsInlineListeners { get; set; }
    
    protected override bool supportsMode(EndpointMode mode)
    {
        if (mode == EndpointMode.Inline) return SupportsInlineListeners;
        return true;
    }
}