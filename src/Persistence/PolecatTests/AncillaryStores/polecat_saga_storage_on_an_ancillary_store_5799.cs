using IntegrationTests;
using JasperFx.Resources;
using Polecat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

/// <summary>
///     GH-5799, the Polecat mirror of <c>MartenTests.AncillaryStores.saga_storage_on_an_ancillary_store_5799</c>. A
///     saga stored in an ancillary Polecat store was claimed by the main store's saga diagnostics, so every read of it
///     went to the main store. Each store's diagnostics now own the sagas routed to that store.
/// </summary>
public class polecat_saga_storage_on_an_ancillary_store_5799 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "pc_saga_5799_main";
                    })
                    .IntegrateWithWolverine();

                opts.Services.AddPolecatStore<IPcGiftSagaStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "pc_saga_5799_other";
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PcOrderLedgerSaga>()
                    .IncludeType<PcGiftLedgerSaga>();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theHost.Services.GetRequiredService<IDocumentStore>().Advanced.Clean.DeleteAllDocumentsAsync();
        await theHost.Services.GetRequiredService<IPcGiftSagaStore>().Advanced.Clean.DeleteAllDocumentsAsync();

        await theHost.InvokeMessageAndWaitAsync(new StartPcOrderLedger("o-1"));
        await theHost.InvokeMessageAndWaitAsync(new StartPcGiftLedger("g-1"));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task the_ancillary_saga_is_in_the_ancillary_store()
    {
        await using var session = theHost.Services.GetRequiredService<IPcGiftSagaStore>().QuerySession();
        (await session.LoadAsync<PcGiftLedgerSaga>("g-1", TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task each_saga_type_is_registered_once()
    {
        var registered = await theHost.GetRuntime().SagaStorage.GetRegisteredSagasAsync(TestContext.Current.CancellationToken);

        registered.Count(x => x.StateType.FullName == typeof(PcOrderLedgerSaga).FullName).ShouldBe(1);
        registered.Count(x => x.StateType.FullName == typeof(PcGiftLedgerSaga).FullName).ShouldBe(1);
    }

    [Fact]
    public async Task lists_the_ancillary_stores_saga()
    {
        var instances = await theHost.GetRuntime().SagaStorage
            .ListSagaInstancesAsync(typeof(PcGiftLedgerSaga).FullName!, 10, TestContext.Current.CancellationToken);

        instances.Select(x => x.Identity.ToString()).ShouldBe(["g-1"]);
    }

    [Fact]
    public async Task reads_the_ancillary_stores_saga()
    {
        var state = await theHost.GetRuntime().SagaStorage
            .ReadSagaAsync(typeof(PcGiftLedgerSaga).FullName!, "g-1", TestContext.Current.CancellationToken);

        state.ShouldNotBeNull();
        state.Identity.ShouldBe("g-1");
    }

    [Fact]
    public async Task queries_the_ancillary_stores_saga()
    {
        var result = await theHost.GetRuntime().SagaStorage
            .QuerySagasAsync(typeof(PcGiftLedgerSaga).FullName!, new JasperFx.Documents.DocumentQueryOptions(1, 10),
                TestContext.Current.CancellationToken);

        result.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task still_reads_the_main_stores_saga()
    {
        var storage = theHost.GetRuntime().SagaStorage;

        (await storage.ReadSagaAsync(typeof(PcOrderLedgerSaga).FullName!, "o-1", TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        (await storage.ListSagaInstancesAsync(typeof(PcOrderLedgerSaga).FullName!, 10, TestContext.Current.CancellationToken))
            .Select(x => x.Identity.ToString()).ShouldBe(["o-1"]);
    }
}

public interface IPcGiftSagaStore : IDocumentStore;

public record StartPcOrderLedger(string Id);

public record StartPcGiftLedger(string Id);

public class PcOrderLedgerSaga : Wolverine.Saga
{
    public string Id { get; set; } = string.Empty;

    public static PcOrderLedgerSaga Start(StartPcOrderLedger command) => new() { Id = command.Id };
}

[PolecatStore(typeof(IPcGiftSagaStore))]
public class PcGiftLedgerSaga : Wolverine.Saga
{
    public string Id { get; set; } = string.Empty;

    public static PcGiftLedgerSaga Start(StartPcGiftLedger command) => new() { Id = command.Id };
}
