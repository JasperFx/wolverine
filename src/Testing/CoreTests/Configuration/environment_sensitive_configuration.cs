using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Configuration;

public class environment_sensitive_configuration
{

    [Fact]
    public async Task optimized_mode_takes_local_development_env_name()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.CritterStackDefaults(x =>
                {
                    // Somebody did want this, so you can actually change the name
                    // of the "development" environment
                    x.DevelopmentEnvironmentName = "LocalDevEnvironment";
                    
                    x.Production.GeneratedCodeMode = TypeLoadMode.Static;
                    x.Production.ResourceAutoCreate = AutoCreate.None;

                    // Little draconian, but this might be helpful
                    x.Production.AssertAllPreGeneratedTypesExist = true;

                    // These are defaults, but showing for completeness
                    x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
                    x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
                });
            })
            .UseEnvironment("LocalDevEnvironment")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Auto);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeTrue();
        options.AutoBuildMessageStorageOnStartup.ShouldBe(AutoCreate.CreateOrUpdate);
    }

    [Fact]
    public async Task optimized_mode_defaults_to_develop_as_local_development_env_name()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.CritterStackDefaults(x =>
                {
                    x.Production.GeneratedCodeMode = TypeLoadMode.Static;
                    x.Production.ResourceAutoCreate = AutoCreate.None;

                    // Little draconian, but this might be helpful
                    x.Production.AssertAllPreGeneratedTypesExist = true;

                    // These are defaults, but showing for completeness
                    x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
                    x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
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
    public async Task optimized_mode_uses_prod_config_for_non_local_env()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.CritterStackDefaults(x =>
                {
                    // Somebody did want this, so you can actually change the name
                    // of the "development" environment
                    x.DevelopmentEnvironmentName = "LocalDevEnvironment_Bogus";
                    
                    x.Production.GeneratedCodeMode = TypeLoadMode.Static;
                    x.Production.ResourceAutoCreate = AutoCreate.None;

                    // Little draconian, but this might be helpful
                    x.Production.AssertAllPreGeneratedTypesExist = true;

                    // These are defaults, but showing for completeness
                    x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
                    x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
                });
                
            })
            .UseEnvironment("LocalDevEnvironment")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Auto);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeFalse();
        options.AutoBuildMessageStorageOnStartup.ShouldBe(AutoCreate.None);
    }

    [Fact]
    public async Task optimized_mode_uses_given_prod_config_for_non_local_env()
    {
        const TypeLoadMode prodTypeLoadMode = TypeLoadMode.Static;

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.CritterStackDefaults(x =>
                {
                    // Somebody did want this, so you can actually change the name
                    // of the "development" environment
                    x.DevelopmentEnvironmentName = "LocalDevEnvironment_Bogus";
                    
                    x.Production.GeneratedCodeMode = prodTypeLoadMode;
                    x.Production.ResourceAutoCreate = AutoCreate.None;

                    // Little draconian, but this might be helpful
                    x.Production.AssertAllPreGeneratedTypesExist = true;

                    // These are defaults, but showing for completeness
                    x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
                    x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
                });
                
            })
            .UseEnvironment("LocalDevEnvironment")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(prodTypeLoadMode);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeFalse();
        options.AutoBuildMessageStorageOnStartup.ShouldBe(AutoCreate.None);
    }
}

public class RegisteredMarker
{
    public string Name { get; set; }
}