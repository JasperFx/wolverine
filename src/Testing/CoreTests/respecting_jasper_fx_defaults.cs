using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests;

public class respecting_jasper_fx_defaults
{
    [Fact]
    public async Task use_defaults()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .UseEnvironment("Development")
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        runtime.Options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Dynamic);
        runtime.Options.ServiceName.ShouldBe("CoreTests");
        
        // TODO -- check AutoCreate here too
    }

    [Fact]
    public async Task use_jasper_fx_defaults()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // GH-4151: TypeLoadMode.Static now asserts at startup that every handler chain's pre-built
                // type is really in the application assembly. This test only cares that the option was
                // propagated, and CoreTests has no pre-generated types, so give it no chains to check.
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.CritterStackDefaults(cr =>
                {
                    cr.ServiceName = "Special";
                    cr.Development.GeneratedCodeMode = TypeLoadMode.Static;
                    
                    // TODO -- also do AutoCreate
                });
            })
            .UseEnvironment("Development")
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        
        var runtime = host.GetRuntime();
        
        runtime.Options.ServiceName.ShouldBe("Special");
        runtime.Options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Static);
    }
}