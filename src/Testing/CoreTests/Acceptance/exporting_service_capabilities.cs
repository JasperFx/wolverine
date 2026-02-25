using Wolverine.Configuration;
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

    [Fact]
    public void should_not_include_messages_from_wolverine_assembly()
    {
        var wolverineAssemblyName = typeof(WolverineOptions).Assembly.GetName().Name;
        theCapabilities.Messages.ShouldNotBeEmpty();
        theCapabilities.Messages.ShouldAllBe(m => m.Type.AssemblyName != wolverineAssemblyName);
    }

    [Fact]
    public void should_not_include_system_endpoints()
    {
        var systemEndpointUris = Host.GetRuntime().Options.Transports
            .AllEndpoints()
            .Where(e => e.Role == EndpointRole.System)
            .Select(e => e.Uri)
            .ToList();

        systemEndpointUris.ShouldNotBeEmpty();
        foreach (var uri in systemEndpointUris)
        {
            theCapabilities.MessagingEndpoints.ShouldNotContain(e => e.Uri == uri);
        }
    }

    [Fact]
    public void should_include_application_messages()
    {
        var appAssemblyName = GetType().Assembly.GetName().Name;
        theCapabilities.Messages.ShouldContain(m => m.Type.AssemblyName == appAssemblyName);
    }
}