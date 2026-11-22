using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
///     GH-5799, the Fisher mirror of <c>MartenTests.AncillaryStores.saga_storage_on_an_ancillary_store_5799</c>. A
///     saga stored in an ancillary Fisher store was claimed by the main store's saga diagnostics, so every read of it
///     went to the main store's SQLite file. Each store's diagnostics now own the sagas routed to that store.
/// </summary>
public class fisher_saga_storage_on_an_ancillary_store_5799 : IAsyncLifetime
{
    private FisherTestDatabase theAncillaryDatabase = null!;
    private FisherTestDatabase theMainDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theMainDatabase = Servers.CreateDatabase("saga_5799_main");
        theAncillaryDatabase = Servers.CreateDatabase("saga_5799_other");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theMainDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Services.AddFisherStore<IFiGiftSagaStore>(m =>
                    {
                        m.Connection(theAncillaryDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FiOrderLedgerSaga>()
                    .IncludeType<FiGiftLedgerSaga>();
            }).StartAsync();

        await theHost.InvokeMessageAndWaitAsync(new StartFiOrderLedger("o-1"));
        await theHost.InvokeMessageAndWaitAsync(new StartFiGiftLedger("g-1"));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theMainDatabase.Dispose();
        theAncillaryDatabase.Dispose();
    }

    [Fact]
    public async Task the_ancillary_saga_is_in_the_ancillary_store()
    {
        await using var session = theHost.Services.GetRequiredService<IFiGiftSagaStore>().QuerySession();
        (await session.LoadAsync<FiGiftLedgerSaga>("g-1", TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task each_saga_type_is_registered_once()
    {
        var registered = await theHost.GetRuntime().SagaStorage.GetRegisteredSagasAsync(TestContext.Current.CancellationToken);

        registered.Count(x => x.StateType.FullName == typeof(FiOrderLedgerSaga).FullName).ShouldBe(1);
        registered.Count(x => x.StateType.FullName == typeof(FiGiftLedgerSaga).FullName).ShouldBe(1);
    }

    [Fact]
    public async Task lists_the_ancillary_stores_saga()
    {
        var instances = await theHost.GetRuntime().SagaStorage
            .ListSagaInstancesAsync(typeof(FiGiftLedgerSaga).FullName!, 10, TestContext.Current.CancellationToken);

        instances.Select(x => x.Identity.ToString()).ShouldBe(["g-1"]);
    }

    [Fact]
    public async Task reads_the_ancillary_stores_saga()
    {
        var state = await theHost.GetRuntime().SagaStorage
            .ReadSagaAsync(typeof(FiGiftLedgerSaga).FullName!, "g-1", TestContext.Current.CancellationToken);

        state.ShouldNotBeNull();
        state.Identity.ShouldBe("g-1");
    }

    [Fact]
    public async Task queries_the_ancillary_stores_saga()
    {
        var result = await theHost.GetRuntime().SagaStorage
            .QuerySagasAsync(typeof(FiGiftLedgerSaga).FullName!, new JasperFx.Documents.DocumentQueryOptions(1, 10),
                TestContext.Current.CancellationToken);

        result.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task still_reads_the_main_stores_saga()
    {
        var storage = theHost.GetRuntime().SagaStorage;

        (await storage.ReadSagaAsync(typeof(FiOrderLedgerSaga).FullName!, "o-1", TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        (await storage.ListSagaInstancesAsync(typeof(FiOrderLedgerSaga).FullName!, 10, TestContext.Current.CancellationToken))
            .Select(x => x.Identity.ToString()).ShouldBe(["o-1"]);
    }
}

public interface IFiGiftSagaStore : IDocumentStore;

public record StartFiOrderLedger(string Id);

public record StartFiGiftLedger(string Id);

public class FiOrderLedgerSaga : Wolverine.Saga
{
    public string Id { get; set; } = string.Empty;

    public static FiOrderLedgerSaga Start(StartFiOrderLedger command) => new() { Id = command.Id };
}

[FisherStore(typeof(IFiGiftSagaStore))]
public class FiGiftLedgerSaga : Wolverine.Saga
{
    public string Id { get; set; } = string.Empty;

    public static FiGiftLedgerSaga Start(StartFiGiftLedger command) => new() { Id = command.Id };
}
