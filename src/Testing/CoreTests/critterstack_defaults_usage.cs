using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Module1;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests;

public class critterstack_defaults_usage
{
    [Fact]
    public async Task running_in_development_mode()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.CritterStackDefaults(x =>
                {
                    x.Development.SourceCodeWritingEnabled = true;
                    x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
                    x.Development.GeneratedCodeMode = TypeLoadMode.Auto;
                });
            })
            .UseEnvironment("Development")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Auto);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeTrue();
        options.AutoBuildMessageStorageOnStartup.ShouldBe(AutoCreate.CreateOrUpdate);
    }

    [Fact]
    public async Task set_the_application_assembly()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.CritterStackDefaults(x =>
                {
                    x.Development.SourceCodeWritingEnabled = true;
                    x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
                    x.Development.GeneratedCodeMode = TypeLoadMode.Auto;

                    x.ApplicationAssembly = typeof(IInterfaceMessage).Assembly;
                });
            })
            .UseEnvironment("Development")
            .StartAsync();
        
        host.GetRuntime().Options.ApplicationAssembly.ShouldBe(typeof(IInterfaceMessage).Assembly);
    }

    [Fact]
    public async Task use_the_default_application_assembly()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {

            })
            .UseEnvironment("Development")
            .StartAsync();
        
        host.GetRuntime().Options.ApplicationAssembly.ShouldBe(GetType().Assembly);
    }

    [Fact]
    public async Task running_in_production_mode_1()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.CritterStackDefaults(x =>
                {
                    x.Production.GeneratedCodeMode = TypeLoadMode.Auto;
                    x.Production.SourceCodeWritingEnabled = false;
                    x.Production.ResourceAutoCreate = AutoCreate.None;
                });
            })
            .UseEnvironment("Production")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Auto);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeFalse();
        options.AutoBuildMessageStorageOnStartup.ShouldBe(AutoCreate.None);
    }

    [Fact]
    public async Task running_in_production_mode_2()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.CritterStackDefaults(x =>
                {
                    x.Production.GeneratedCodeMode = TypeLoadMode.Static;
                    x.Production.SourceCodeWritingEnabled = false;
                    x.Production.ResourceAutoCreate = AutoCreate.None;
                });
            })
            .UseEnvironment("Production")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Static);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeFalse();
        options.AutoBuildMessageStorageOnStartup.ShouldBe(AutoCreate.None);
    }
}