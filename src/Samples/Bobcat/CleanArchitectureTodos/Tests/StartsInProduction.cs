using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace CleanArchitectureTodos.Tests;

// GH-5703: a plain `dotnet run` of this sample runs in Production, where Wolverine 7.0 expects pre-generated code.
// The sample says to compile at runtime there instead, so it starts. Every spec here runs against the host in
// Production. A test host compiles at runtime either way, so that every sample sets the mode (or ships Development
// launch settings) is checked across the repository by CoreTests' every_sample_says_how_it_runs_its_generated_code_5703
public class starts_in_production
{
    private readonly CleanArchitectureTodosHost _app;

    public starts_in_production(CleanArchitectureTodosHost app) => _app = app;

    [Fact]
    public void the_sample_compiles_its_generated_code_at_runtime_in_production()
    {
        Assert.Equal(Environments.Production, _app.Host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.Equal(TypeLoadMode.Dynamic, _app.Host.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode);
    }
}
