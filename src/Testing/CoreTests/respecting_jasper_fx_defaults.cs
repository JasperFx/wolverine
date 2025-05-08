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
            .StartAsync();

        var runtime = host.GetRuntime();
        runtime.Options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Dynamic);
        runtime.Options.ServiceName.ShouldBe("CoreTests");
        
        // TODO -- check AutoCreate here too
    }

    // TODO -- come back to this
    // [Fact]
    // public async Task use_jasper_fx_defaults()
    // {
    //     using var host = await Host.CreateDefaultBuilder()
    //         .UseWolverine(opts =>
    //         {
    //             opts.Services.CritterStackDefaults(cr =>
    //             {
    //                 cr.ServiceName = "Special";
    //                 cr.Development.GeneratedCodeMode = TypeLoadMode.Static;
    //                 
    //                 // TODO -- also do AutoCreate
    //             });
    //         })
    //         .UseEnvironment("Development")
    //         .StartAsync();
    //     
    //     var runtime = host.GetRuntime();
    //     
    //     runtime.Options.ServiceName.ShouldBe("Special");
    //     runtime.Options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Static);
    // }
}