using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace CqrsMinimalApi.Tests;

// GH-5703: a plain `dotnet run` of this sample runs in Production, where Wolverine 7.0 expects pre-generated code.
// The sample says to compile at runtime there instead, so it starts. Every spec here runs against the host in Production
public class starts_in_production
{
    private readonly CqrsMinimalApiHost _app;

    public starts_in_production(CqrsMinimalApiHost app) => _app = app;

    [Fact]
    public void the_sample_compiles_its_generated_code_at_runtime_in_production()
    {
        Assert.Equal(Environments.Production, _app.Host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.Equal(TypeLoadMode.Auto, _app.Host.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode);
    }
}
