using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Configuration;

internal class OptimizeArtifactWorkflow : IWolverineExtension
{
    private readonly IHostEnvironment _environment;

    public OptimizeArtifactWorkflow(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public void Configure(WolverineOptions options)
    {
        if (_environment.IsDevelopment())
        {
            options.Durability.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            options.Durability.CodeGeneration.SourceCodeWritingEnabled = true;
            options.AutoBuildEnvelopeStorageOnStartup = true;
        }
        else
        {
            options.Durability.CodeGeneration.TypeLoadMode = options.ProductionTypeLoadMode;
            options.Durability.CodeGeneration.SourceCodeWritingEnabled = false;
            options.AutoBuildEnvelopeStorageOnStartup = false;
        }
    }
}