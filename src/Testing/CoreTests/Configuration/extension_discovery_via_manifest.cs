using System.Linq;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Module1;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

// Auto-discovery of [WolverineModule<T>] extensions now flows through the compile-time manifest
// emitted by JasperFx.SourceGenerator (the JasperFx.Generated.DiscoveredExtensions class) instead
// of the old runtime ExtensionLoader + AssemblyFinder filesystem scan. See GH-2902.
public class extension_discovery_via_manifest
{
    private static Task<IHost> startHostAsync()
    {
        // Default ExtensionDiscovery.Automatic; conventional *handler* discovery is off only to keep
        // the host light — it does not affect extension discovery.
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.DisableConventionalDiscovery())
            .StartAsync();
    }

    [Fact]
    public async Task declared_module_extension_is_discovered_from_the_manifest_and_applied()
    {
        using var host = await startHostAsync();

        // Module1 declares [assembly: WolverineModule<Module1Extension>]; Module1Extension.Configure
        // registers IModuleService. Its presence proves the manifest-driven discovery applied it.
        host.GetRuntime().Options.AppliedExtensions
            .ShouldContain(x => x is Module1Extension);

        host.Services.GetRequiredService<IServiceContainer>()
            .HasRegistrationFor<IModuleService>().ShouldBeTrue();
    }

    [Fact]
    public async Task framework_internal_extensions_are_never_auto_applied()
    {
        using var host = await startHostAsync();
        var options = host.GetRuntime().Options;

        // The manifest's marker-interface scan also lists Wolverine's own internal IWolverineExtension
        // helpers (these are applied explicitly / via DI, never auto-discovered). Discovery is gated to
        // each assembly's declared [WolverineModule] type, so none of them may leak in here. Auto-applying
        // DisableExternalTransports in particular would silently stub every external transport.
        options.ExternalTransportsAreStubbed.ShouldBeFalse();

        var applied = options.AppliedExtensions.Select(x => x.GetType().Name).ToArray();
        applied.ShouldNotContain("DisableExternalTransports");
        applied.ShouldNotContain("DisablePersistence");
        applied.ShouldNotContain("UseSoloDurabilityMode");
        applied.ShouldNotContain("LambdaWolverineExtension");
    }
}
