using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using JasperFx;
using JasperFx.CommandLine;
using Wolverine.Runtime;

namespace DiagnosticsTests;

public class AppFixture : IAsyncLifetime
{
    public IAlbaHost? Host { get; private set; }

    public async Task InitializeAsync()
    {
        JasperFxEnvironment.AutoStartHost = true;

        // This is bootstrapping the actual application using
        // its implied Program.Main() set up
        Host = await AlbaHost.For<Program>(x => { });
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
        // Using Marten, wipe out all data and reset the state
        // back to exactly what we described in InitialAccountData
        await Store.Advanced.ResetAllData();
    }

    // This is required because of the IAsyncLifetime
    // interface. Note that I do *not* tear down database
    // state after the test. That's purposeful
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task<IScenarioResult> Scenario(Action<Scenario> configure)
    {
        return Host.Scenario(configure);
    }
}