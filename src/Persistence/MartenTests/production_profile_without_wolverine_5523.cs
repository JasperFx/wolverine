using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;

namespace MartenTests;

/// <summary>
/// GH-5523: the 7.0 Production default of pre-generated code belongs to Wolverine, not to JasperFx. A Marten
/// application without UseWolverine() keeps JasperFx's Production profile, and the same application with Wolverine
/// gets the override. Nothing is started, so no database is needed.
/// </summary>
public class production_profile_without_wolverine_5523
{
    [Fact]
    public void a_marten_host_without_wolverine_is_unchanged()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Production)
            .ConfigureServices(services => services.AddMarten(Servers.PostgresConnectionString))
            .Build();

        var jasperfx = host.Services.GetRequiredService<JasperFxOptions>();
        jasperfx.ActiveProfile.ShouldBeSameAs(jasperfx.Production);
        jasperfx.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        jasperfx.Production.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
    }

    [Fact]
    public void the_same_marten_host_with_wolverine_gets_the_7_0_production_default()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Production)
            .ConfigureServices(services => services.AddMarten(Servers.PostgresConnectionString))
            .UseWolverine(opts =>
            {
                // This process is a test host, where the default is otherwise deliberately skipped
                opts.ApplyProductionCodeGenerationUnderTestRunner = true;
                opts.Discovery.DisableConventionalDiscovery();
            })
            .Build();

        var jasperfx = host.Services.GetRequiredService<JasperFxOptions>();
        jasperfx.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Static);
        jasperfx.Production.AssertAllPreGeneratedTypesExist.ShouldBeTrue();
    }
}
