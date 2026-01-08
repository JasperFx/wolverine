using Wolverine.Configuration.Capabilities;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class exporting_service_capabilities : IntegrationContext, IAsyncLifetime
{
    private ServiceCapabilities theCapabilities;

    public exporting_service_capabilities(DefaultApp @default) : base(@default)
    {
        
    }
    

    public async Task InitializeAsync()
    {
        theCapabilities = await ServiceCapabilities.ReadFrom(Host.GetRuntime(), null, CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public void wolverine_version()
    {
        theCapabilities.WolverineVersion.ShouldBe(typeof(WolverineOptions).Assembly.GetName().Version);
    }

    [Fact]
    public void the_application_version()
    {
        theCapabilities.Version.ShouldBe(GetType().Assembly.GetName().Version);
    }
    
    

}