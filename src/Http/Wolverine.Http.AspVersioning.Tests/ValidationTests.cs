using System.Text.Json;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.AspVersioning.Tests;

/// <summary>
/// The bridge's fail-fast configuration guards (<c>AspVersioningPolicy.validateConfiguration</c>) and the
/// idempotence of the public <see cref="WolverineHttpOptionsExtensions.UseAspVersioning"/> entry point.
/// </summary>
public class ValidationTests
{
    // Build the host-free parent container the policy inspects. Mirrors VersioningHarness, but lets each
    // test choose whether Asp.Versioning is configured and which WolverineHttpOptions is registered.
    private static IServiceContainer Container(
        bool addApiVersioning,
        WolverineHttpOptions httpOptions
    )
    {
        var registry = new ServiceCollection();
        if (addApiVersioning)
            registry.AddApiVersioning();
        registry.AddSingleton<JsonSerializerOptions>();
        registry.AddTransient<IServiceVariableSource, ServiceCollectionServerVariableSource>();
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);
        registry.AddSingleton(httpOptions);

        return registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();
    }

    private static void Apply(IServiceContainer container) =>
        new AspVersioningPolicy().Apply([], new GenerationRules(), container);

    // Guard 1: forgetting AddApiVersioning() is the most likely first-run mistake; the bridge fails fast
    // with a message that names the fix rather than letting the endpoint finalizer blow up cryptically.
    [Fact]
    public void throws_when_AddApiVersioning_was_not_called()
    {
        var container = Container(addApiVersioning: false, new WolverineHttpOptions());

        var ex = Should.Throw<InvalidOperationException>(() => Apply(container));
        ex.Message.ShouldContain("AddApiVersioning()");
    }

    // Guard 2: the native and Asp.Versioning integrations are mutually exclusive; enabling both is a
    // configuration error the bridge rejects up front.
    [Fact]
    public void throws_when_native_versioning_is_also_enabled()
    {
        var httpOptions = new WolverineHttpOptions();
        httpOptions.UseApiVersioning(_ => { }); // turns on Wolverine's native versioning

        var container = Container(addApiVersioning: true, httpOptions);

        var ex = Should.Throw<InvalidOperationException>(() => Apply(container));
        ex.Message.ShouldContain("cannot be enabled simultaneously");
    }

    // UseAspVersioning() registers the policy on the first call and is a no-op afterward.
    [Fact]
    public void use_asp_versioning_is_idempotent()
    {
        var httpOptions = new WolverineHttpOptions();

        httpOptions.UseAspVersioning();
        httpOptions.UseAspVersioning();

        httpOptions.Policies.Count(p => p is AspVersioningPolicy).ShouldBe(1);
    }
}
