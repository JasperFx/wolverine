using System.Linq.Expressions;
using System.Text.Json;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.AspVersioning.Tests;

public class HostlessAspVersioningFixture : IAsyncLifetime
{
    public HttpGraph Parent { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Parent = BuildParent();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Parent = null!;
        return Task.CompletedTask;
    }

    /// <summary>Build a host-free chain whose parent container can satisfy the Asp.Versioning finalizer.</summary>
    public HttpChain ChainFor<T>(Expression<Action<T>> expression) =>
        HttpChain.ChainFor(expression, Parent);

    /// <summary>Run the policy under test over <paramref name="chains"/>.</summary>
    public void Apply(params HttpChain[] chains) =>
        new AspVersioningPolicy().Apply(chains, new GenerationRules(), Parent.Container);

    /// <summary>
    /// Run a single <see cref="AspVersioningPolicy"/> instance over <paramref name="chains"/>
    /// <paramref name="times"/> times. Used to assert the policy is idempotent — a re-run must not
    /// attach a second <c>ApiVersionSet</c> or duplicate providers.
    /// </summary>
    public void ApplyRepeated(int times, params HttpChain[] chains)
    {
        var policy = new AspVersioningPolicy();
        for (var i = 0; i < times; i++)
            policy.Apply(chains, new GenerationRules(), Parent.Container);
    }

    private static HttpGraph BuildParent()
    {
        var registry = new ServiceCollection();
        registry.AddLogging();
        registry.AddApiVersioning();
        registry.AddSingleton<JsonSerializerOptions>();
        registry.AddTransient<IServiceVariableSource, ServiceCollectionServerVariableSource>();
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);
        // AspVersioningPolicy.validateConfiguration reads WolverineHttpOptions to detect a native-versioning
        // conflict; a real MapWolverineEndpoints host always registers it, so the host-free parent must too
        // (a default instance has ApiVersioning == null, i.e. no native versioning — the valid state here).
        registry.AddSingleton(new WolverineHttpOptions());

        var provider = registry.BuildServiceProvider();
        return new HttpGraph(
            new WolverineOptions(),
            provider.GetRequiredService<IServiceContainer>()
        );
    }
}

[CollectionDefinition("hostless")]
public class HostlessAspVersioningCollection : ICollectionFixture<HostlessAspVersioningFixture>;

[Collection("hostless")]
public abstract class HostlessAspVersioningContext
{
    protected HostlessAspVersioningContext(HostlessAspVersioningFixture fixture) =>
        VersioningHarness = fixture;

    protected HostlessAspVersioningFixture VersioningHarness { get; }
}
