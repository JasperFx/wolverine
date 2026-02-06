using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Configuration;

public class generated_code_output_path_configuration
{
    [Fact]
    public void wolverine_options_should_use_jasperfx_generated_code_output_path()
    {
        var jasperfx = new JasperFxOptions
        {
            GeneratedCodeOutputPath = "/custom/path/Generated",
        };

        var wolverineOptions = new WolverineOptions();
        wolverineOptions.ReadJasperFxOptions(jasperfx);

        wolverineOptions.CodeGeneration.GeneratedCodeOutputPath
            .ShouldBe("/custom/path/Generated");
    }

    [Fact]
    public void wolverine_options_should_not_override_explicit_generated_code_output_path()
    {
        var jasperfx = new JasperFxOptions
        {
            GeneratedCodeOutputPath = "/jasperfx/path",
        };

        var wolverineOptions = new WolverineOptions();
        wolverineOptions.CodeGeneration.GeneratedCodeOutputPath = "/explicit/path";
        wolverineOptions.ReadJasperFxOptions(jasperfx);

        // Should keep explicit setting
        wolverineOptions.CodeGeneration.GeneratedCodeOutputPath
            .ShouldBe("/explicit/path");
    }

    [Fact]
    public async Task critter_stack_defaults_generated_code_output_path_flows_to_wolverine()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.CritterStackDefaults(opts =>
                {
                    opts.GeneratedCodeOutputPath = "/test/output/path";
                });
            })
            .UseWolverine()
            .StartAsync();

        var wolverineOptions = host.Services.GetRequiredService<WolverineOptions>();
        wolverineOptions.CodeGeneration.GeneratedCodeOutputPath
            .ShouldBe("/test/output/path");
    }

    [Fact]
    public async Task explicit_wolverine_path_takes_precedence_over_critter_stack_defaults()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.CritterStackDefaults(opts =>
                {
                    opts.GeneratedCodeOutputPath = "/jasperfx/path";
                });
            })
            .UseWolverine(opts =>
            {
                opts.CodeGeneration.GeneratedCodeOutputPath = "/explicit/wolverine/path";
            })
            .StartAsync();

        var wolverineOptions = host.Services.GetRequiredService<WolverineOptions>();
        wolverineOptions.CodeGeneration.GeneratedCodeOutputPath
            .ShouldBe("/explicit/wolverine/path");
    }

    [Fact]
    public void wolverine_options_should_not_copy_null_generated_code_output_path()
    {
        var jasperfx = new JasperFxOptions
        {
            // GeneratedCodeOutputPath is null by default
        };

        var wolverineOptions = new WolverineOptions();
        var defaultPath = wolverineOptions.CodeGeneration.GeneratedCodeOutputPath;
        wolverineOptions.ReadJasperFxOptions(jasperfx);

        // Should keep the default path when JasperFxOptions has null
        wolverineOptions.CodeGeneration.GeneratedCodeOutputPath
            .ShouldBe(defaultPath);
    }
}
