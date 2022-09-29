using LamarCodeGeneration;
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
            options.Advanced.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            options.Advanced.CodeGeneration.SourceCodeWritingEnabled = true;
            options.AutoBuildEnvelopeStorageOnStartup = true;
        }
        else
        {
            options.Advanced.CodeGeneration.TypeLoadMode = options.ProductionTypeLoadMode;
            options.Advanced.CodeGeneration.SourceCodeWritingEnabled = false;
            options.AutoBuildEnvelopeStorageOnStartup = false;
        }
    }
}