using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5523 (7.0, from #5344): a Wolverine application in the Production profile defaults to pre-generated code,
/// <see cref="TypeLoadMode.Static"/> with <see cref="Profile.AssertAllPreGeneratedTypesExist"/> on. JasperFx's shared
/// default is untouched, an explicit setting always wins, and <see cref="WolverineOptions.RestoreV6Defaults"/> puts
/// back the 6.x behaviour.
///
/// <para>This process is a test host, where Wolverine deliberately leaves the profile alone (see
/// <see cref="the_default_is_not_applied_under_a_test_runner"/>), so the other tests force it on with the internal
/// <c>ApplyProductionCodeGenerationUnderTestRunner</c> hook.</para>
/// </summary>
public class production_profile_uses_pre_generated_code_5523
{
    private static IHost buildHost(string environment, Action<WolverineOptions>? configure = null,
        Action<IServiceCollection>? before = null, Action<IServiceCollection>? after = null,
        bool applyUnderTestRunner = true)
    {
        return Host.CreateDefaultBuilder()
            .UseEnvironment(environment)
            .ConfigureServices(services => before?.Invoke(services))
            .UseWolverine(opts =>
            {
                opts.ApplyProductionCodeGenerationUnderTestRunner = applyUnderTestRunner;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<Ping5523Handler>();
                configure?.Invoke(opts);
            })
            .ConfigureServices(services => after?.Invoke(services))
            .Build();
    }

    [Fact]
    public void production_profile_is_static_and_asserts_pre_generated_types()
    {
        using var host = buildHost(Environments.Production);

        var jasperfx = host.Services.GetRequiredService<JasperFxOptions>();
        jasperfx.ActiveProfile.ShouldBeSameAs(jasperfx.Production);
        jasperfx.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Static);
        jasperfx.Production.AssertAllPreGeneratedTypesExist.ShouldBeTrue();

        // Every Wolverine chain (handlers, HTTP endpoints, gRPC services, and the Marten/Polecat/Fisher chains,
        // which are handler or HTTP chains) generates under WolverineOptions.CodeGeneration
        host.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode
            .ShouldBe(TypeLoadMode.Static);
    }

    [Fact]
    public void development_profile_is_unchanged()
    {
        using var host = buildHost(Environments.Development);

        var jasperfx = host.Services.GetRequiredService<JasperFxOptions>();
        jasperfx.ActiveProfile.ShouldBeSameAs(jasperfx.Development);
        jasperfx.Development.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        jasperfx.Development.AssertAllPreGeneratedTypesExist.ShouldBeFalse();

        host.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode
            .ShouldBe(TypeLoadMode.Dynamic);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void an_explicit_production_mode_wins_even_when_it_is_jasperfx_default(bool registeredBeforeWolverine)
    {
        Action<IServiceCollection> explicitDynamic =
            s => s.CritterStackDefaults(x => x.Production.GeneratedCodeMode = TypeLoadMode.Dynamic);

        using var host = buildHost(Environments.Production,
            before: registeredBeforeWolverine ? explicitDynamic : null,
            after: registeredBeforeWolverine ? null : explicitDynamic);

        var jasperfx = host.Services.GetRequiredService<JasperFxOptions>();
        jasperfx.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);

        // The assertion default only goes with Static, so it is withdrawn along with it
        jasperfx.Production.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
    }

    [Fact]
    public void an_explicit_auto_mode_wins()
    {
        using var host = buildHost(Environments.Production, after: s => s.CritterStackDefaults(x =>
        {
            x.Production.GeneratedCodeMode = TypeLoadMode.Auto;
        }));

        host.Services.GetRequiredService<JasperFxOptions>().Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Auto);
        host.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode
            .ShouldBe(TypeLoadMode.Auto);
    }

    [Fact]
    public void an_explicit_assertion_setting_wins()
    {
        using var host = buildHost(Environments.Production,
            before: s => s.CritterStackDefaults(x => x.Production.AssertAllPreGeneratedTypesExist = false));

        var jasperfx = host.Services.GetRequiredService<JasperFxOptions>();
        jasperfx.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Static);
        jasperfx.Production.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
    }

    [Fact]
    public void an_explicit_wolverine_type_load_mode_still_wins()
    {
        using var host = buildHost(Environments.Production,
            opts => opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto);

        host.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode
            .ShouldBe(TypeLoadMode.Auto);
    }

    [Fact]
    public void RestoreV6Defaults_in_UseWolverine_keeps_runtime_compilation_in_production()
    {
        using var host = buildHost(Environments.Production, opts => opts.RestoreV6Defaults());

        var jasperfx = host.Services.GetRequiredService<JasperFxOptions>();
        jasperfx.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        jasperfx.Production.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
        host.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode
            .ShouldBe(TypeLoadMode.Dynamic);
    }

    [Fact]
    public void the_default_is_not_applied_under_a_test_runner()
    {
        // Decision on GH-5523: a test host built with Host.CreateDefaultBuilder() and no DOTNET_ENVIRONMENT is in
        // the Production environment with no pre-generated code, so the default would fail every such test
        using var host = buildHost(Environments.Production, applyUnderTestRunner: false);

        var jasperfx = host.Services.GetRequiredService<JasperFxOptions>();
        jasperfx.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        jasperfx.Production.AssertAllPreGeneratedTypesExist.ShouldBeFalse();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        options.ProductionCodeGenerationSkippedForTestRunner.ShouldBeTrue();
        options.CodeGeneration.TypeLoadMode.ShouldBe(TypeLoadMode.Dynamic);
    }

    [Fact]
    public void a_host_without_wolverine_keeps_the_jasperfx_production_profile()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Production)
            .ConfigureServices(s => s.AddJasperFx())
            .Build();

        var jasperfx = host.Services.GetRequiredService<JasperFxOptions>();
        jasperfx.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        jasperfx.Production.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
    }

    [Fact]
    public async Task a_production_app_without_pre_generated_code_fails_at_startup_with_the_way_out()
    {
        var ex = await Should.ThrowAsync<MissingPreBuiltTypesException>(async () =>
        {
            using var host = buildHost(Environments.Production);
            await host.StartAsync(TestContext.Current.CancellationToken);
        });

        // Names the missing types...
        ex.Message.ShouldContain(nameof(Ping5523));

        // ...and both ways out
        ex.Message.ShouldContain("dotnet run -- codegen write");
        ex.Message.ShouldContain("RestoreV6Defaults()");
    }
}

public record Ping5523;

public class Ping5523Handler
{
    public static void Handle(Ping5523 ping)
    {
    }
}
