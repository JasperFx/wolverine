using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Configuration;

public class environment_sensitive_configuration
{
    [Fact]
    public void can_use_hosting_as_part_of_the_configuration()
    {
        var builder = Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                if (context.HostingEnvironment.IsDevelopment())
                {
                    opts.Services.AddSingleton(new RegisteredMarker { Name = "Kendall Fuller" });
                }

                if (context.HostingEnvironment.IsStaging())
                {
                    opts.Services.AddSingleton(new RegisteredMarker { Name = "Darrel Williams" });
                }
            })
            .UseEnvironment("Development");

        using (var host = builder.Build())
        {
            host.Services.GetRequiredService<RegisteredMarker>()
                .Name.ShouldBe("Kendall Fuller");
        }
    }

    [Fact]
    public async Task optimized_mode_takes_local_development_env_name()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) => { opts.OptimizeArtifactWorkflow("LocalDevEnvironment"); })
            .UseEnvironment("LocalDevEnvironment")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Auto);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeTrue();
        options.AutoBuildMessageStorageOnStartup.ShouldBeTrue();
    }

    [Fact]
    public async Task optimized_mode_defaults_to_develop_as_local_development_env_name()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) => { opts.OptimizeArtifactWorkflow(); })
            .UseEnvironment("Development")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Auto);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeTrue();
        options.AutoBuildMessageStorageOnStartup.ShouldBeTrue();
    }

    [Fact]
    public async Task optimized_mode_uses_prod_config_for_non_local_env()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) => { opts.OptimizeArtifactWorkflow("LocalDevEnvironment_Bogus"); })
            .UseEnvironment("LocalDevEnvironment")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Auto);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeFalse();
        options.AutoBuildMessageStorageOnStartup.ShouldBeFalse();
    }

    [Fact]
    public async Task optimized_mode_uses_given_prod_config_for_non_local_env()
    {
        const TypeLoadMode prodTypeLoadMode = TypeLoadMode.Static;

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                opts.OptimizeArtifactWorkflow("LocalDevEnvironment_Bogus", prodTypeLoadMode);
            })
            .UseEnvironment("LocalDevEnvironment")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.CodeGeneration.TypeLoadMode.ShouldBe(prodTypeLoadMode);
        options.CodeGeneration.SourceCodeWritingEnabled.ShouldBeFalse();
        options.AutoBuildMessageStorageOnStartup.ShouldBeFalse();
    }  
}

public class RegisteredMarker
{
    public string Name { get; set; }
}