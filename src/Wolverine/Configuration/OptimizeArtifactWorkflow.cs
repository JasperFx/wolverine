using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Configuration;

internal class OptimizeArtifactWorkflow : IWolverineExtension
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _developmentEnvironment;

    public OptimizeArtifactWorkflow(IServiceProvider serviceProvider, string developmentEnvironment = "Development")
    {
        _serviceProvider = serviceProvider;
        _developmentEnvironment = developmentEnvironment;
    }

    public void Configure(WolverineOptions options)
    {
        var environment = _serviceProvider.GetRequiredService<IHostEnvironment>();
        
        if (environment.IsEnvironment(_developmentEnvironment))
        {
            options.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            options.CodeGeneration.SourceCodeWritingEnabled = true;
            options.AutoBuildMessageStorageOnStartup = true;
        }
        else
        {
            options.CodeGeneration.TypeLoadMode = options.ProductionTypeLoadMode;
            options.CodeGeneration.SourceCodeWritingEnabled = false;
            options.AutoBuildMessageStorageOnStartup = false;
        }
    }
}