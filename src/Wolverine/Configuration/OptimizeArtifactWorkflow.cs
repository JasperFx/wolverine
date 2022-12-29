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
            options.Node.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            options.Node.CodeGeneration.SourceCodeWritingEnabled = true;
            options.AutoBuildEnvelopeStorageOnStartup = true;
        }
        else
        {
            options.Node.CodeGeneration.TypeLoadMode = options.ProductionTypeLoadMode;
            options.Node.CodeGeneration.SourceCodeWritingEnabled = false;
            options.AutoBuildEnvelopeStorageOnStartup = false;
        }
    }
}