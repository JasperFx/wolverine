using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Xunit;

namespace ProcessManagerViaHandlers.Tests;

public class AppFixture : IAsyncLifetime
{
    /// <summary>
    ///     The Marten data and Wolverine's persisted messages. The start handler schedules a 15 minute
    ///     <c>PaymentTimeout</c> that outlives the test, and the run, that scheduled it: left in the inbox, it fires in
    ///     a later run, after its stream was wiped, and its handler gets no state.
    /// </summary>
    public async Task ResetAsync()
    {
        await Host!.ResetAllMartenDataAsync();
        await Host!.Services.GetRequiredService<IMessageStore>().Admin.ClearAllAsync();
    }

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

                // Under xUnit v3 the test project is the entry assembly, which Wolverine would
                // otherwise adopt as the application assembly -- and find no handlers in
                services.ConfigureWolverine(opts => opts.ApplicationAssembly = typeof(Program).Assembly);
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
        await _fixture.ResetAsync();
    }

    public async ValueTask DisposeAsync() =>await  ValueTask.CompletedTask;
}
