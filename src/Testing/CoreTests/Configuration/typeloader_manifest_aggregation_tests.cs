using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using TypeLoaderManifestModuleA;
using TypeLoaderManifestModuleB;
using Xunit;

namespace CoreTests.Configuration;

// Regression coverage for #2632.
//
// Bug: since 5.34.0, WolverineRuntime only inspects [WolverineTypeManifest] on
// Options.ApplicationAssembly when picking the source-generated IWolverineTypeLoader.
// Handlers in *referenced* assemblies that also carry [WolverineTypeManifest] are
// silently dropped (they would surface as IndeterminateRoutesException on first
// invocation). opts.Discovery.IncludeAssembly(...) is ignored on the source-gen
// path; the runtime-scanning path still works.
//
// Fixture assemblies TypeLoaderManifestModuleA and TypeLoaderManifestModuleB each
// carry [assembly: WolverineTypeManifest(typeof(...))] and a stub IWolverineTypeLoader
// that lists exactly one handler type. The test below pins down that — when both
// assemblies are presented to the host (one as ApplicationAssembly, one via
// IncludeAssembly) — handlers from BOTH assemblies appear in the HandlerGraph.
// Pre-fix it fails because only the ApplicationAssembly's loader is consulted.
public class typeloader_manifest_aggregation_tests
{
    [Fact]
    public async Task aggregates_typeloader_manifests_across_application_and_discovery_assemblies()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(ModuleATypeLoader).Assembly;
                opts.Discovery.IncludeAssembly(typeof(ModuleBTypeLoader).Assembly);
            })
            .StartAsync();

        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        var chains = runtime.Handlers.Chains;

        chains.Any(c => c.MessageType == typeof(ModuleAMessage))
            .ShouldBeTrue("Handler from the application assembly should be discovered");

        chains.Any(c => c.MessageType == typeof(ModuleBMessage))
            .ShouldBeTrue(
                "Handler from a referenced assembly with its own [WolverineTypeManifest] " +
                "should be discovered too — it's currently dropped on the source-gen path. See #2632.");
    }
}
