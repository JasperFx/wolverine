using Alba;
using JasperFx;
using JasperFx.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace PolecatIncidentService.Tests;

public class AppFixture : IAsyncLifetime
{
    public IAlbaHost? Host { get; private set; }

    public async Task InitializeAsync()
    {
        JasperFxEnvironment.AutoStartHost = true;

        Host = await AlbaHost.For<Program>(x =>
        {
            x.ConfigureServices(services =>
            {
                services.DisableAllExternalWolverineTransports();
                services.RunWolverineInSoloMode();
            });
        });
    }

    public async Task DisposeAsync()
    {
        await Host!.StopAsync();
        Host.Dispose();
    }
}

[CollectionDefinition("integration")]
public class IntegrationCollection : ICollectionFixture<AppFixture>;

[Collection("integration")]
public abstract class IntegrationContext : IAsyncLifetime
{
    private readonly AppFixture _fixture;

    protected IntegrationContext(AppFixture fixture)
    {
        _fixture = fixture;
        Runtime = (WolverineRuntime)fixture.Host!.Services.GetRequiredService<IWolverineRuntime>();
    }

    public WolverineRuntime Runtime { get; }

    public IAlbaHost Host => _fixture.Host!;
    public IDocumentStore Store => _fixture.Host!.Services.GetRequiredService<IDocumentStore>();

    async Task IAsyncLifetime.InitializeAsync()
    {
        await Host.ResetAllPolecatDataAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task<IScenarioResult> Scenario(Action<Scenario> configure)
    {
        return Host.Scenario(configure);
    }

    protected async Task<(ITrackedSession, IScenarioResult)> TrackedHttpCall(Action<Scenario> configuration)
    {
        IScenarioResult result = null!;

        var tracked = await Host.ExecuteAndWaitAsync(async () =>
        {
            result = await Host.Scenario(configuration);
        });

        return (tracked, result);
    }
}
