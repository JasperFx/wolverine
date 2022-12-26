using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests;

public class optimizing_artifact_workflow
{
    [Fact]
    public async Task running_in_development_mode()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.OptimizeArtifactWorkflow(); })
            .UseEnvironment("Development")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.Advanced.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Auto);
        options.Advanced.CodeGeneration.SourceCodeWritingEnabled.ShouldBeTrue();
        options.AutoBuildEnvelopeStorageOnStartup.ShouldBeTrue();
    }

    [Fact]
    public async Task running_in_production_mode_1()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.OptimizeArtifactWorkflow(); })
            .UseEnvironment("Production")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.Advanced.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Auto);
        options.Advanced.CodeGeneration.SourceCodeWritingEnabled.ShouldBeFalse();
        options.AutoBuildEnvelopeStorageOnStartup.ShouldBeFalse();
    }

    [Fact]
    public async Task running_in_production_mode_2()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.OptimizeArtifactWorkflow(TypeLoadMode.Static); })
            .UseEnvironment("Production")
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.Advanced.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Static);
        options.Advanced.CodeGeneration.SourceCodeWritingEnabled.ShouldBeFalse();
        options.AutoBuildEnvelopeStorageOnStartup.ShouldBeFalse();
    }
}