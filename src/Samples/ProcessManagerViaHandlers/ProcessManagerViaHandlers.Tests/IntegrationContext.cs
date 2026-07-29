using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace ProcessManagerViaHandlers.Tests;

public class AppFixture : IAsyncLifetime
{
    public IAlbaHost? Host { get; private set; }

    public async ValueTask InitializeAsync()
    {
        Host = await AlbaHost.For<Program>(x =>
        {
            x.ConfigureServices(services =>
            {
                // Strip any external transports; this sample only exercises local handlers.
                services.DisableAllExternalWolverineTransports();

                // Required when running Marten + Wolverine in a single test process.
                services.MartenDaemonModeIsSolo();
                services.RunWolverineInSoloMode();
            });
        });
    }

    public async ValueTask DisposeAsync()
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

    async ValueTask IAsyncLifetime.InitializeAsync()
    {
        await Host.ResetAllMartenDataAsync();
    }

    public async ValueTask DisposeAsync() =>await  ValueTask.CompletedTask;
}
