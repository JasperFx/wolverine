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
            options.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            options.CodeGeneration.SourceCodeWritingEnabled = true;
            options.AutoBuildEnvelopeStorageOnStartup = true;
        }
        else
        {
            options.CodeGeneration.TypeLoadMode = options.ProductionTypeLoadMode;
            options.CodeGeneration.SourceCodeWritingEnabled = false;
            options.AutoBuildEnvelopeStorageOnStartup = false;
        }
    }
}