using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

/// <summary>
///     GH-5799. A saga stored in an ancillary Marten store was listed by <c>IWolverineRuntime.SagaStorage</c>, but
///     the main store's diagnostics claimed it, so every read went to the main store: an empty list and a null read
///     for instances that exist. Each store's diagnostics now own the sagas routed to that store.
/// </summary>
public class saga_storage_on_an_ancillary_store_5799 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "saga_storage_5799_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IGiftSagaStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "saga_storage_5799_other";
                }).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderLedgerSaga>()
                    .IncludeType<GiftLedgerSaga>();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theHost.Services.GetRequiredService<IDocumentStore>().Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(OrderLedgerSaga));
        await theHost.Services.GetRequiredService<IGiftSagaStore>().Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(GiftLedgerSaga));

        await theHost.InvokeMessageAndWaitAsync(new StartOrderLedger("o-1"));
        await theHost.InvokeMessageAndWaitAsync(new StartGiftLedger("g-1"));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task the_ancillary_saga_is_in_the_ancillary_store()
    {
        await using var session = theHost.Services.GetRequiredService<IGiftSagaStore>().QuerySession();
        (await session.LoadAsync<GiftLedgerSaga>("g-1", TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task each_saga_type_is_registered_once()
    {
        var registered = await theHost.GetRuntime().SagaStorage.GetRegisteredSagasAsync(TestContext.Current.CancellationToken);

        registered.Count(x => x.StateType.FullName == typeof(OrderLedgerSaga).FullName).ShouldBe(1);
        registered.Count(x => x.StateType.FullName == typeof(GiftLedgerSaga).FullName).ShouldBe(1);
    }

    [Fact]
    public async Task lists_the_ancillary_stores_saga()
    {
        var instances = await theHost.GetRuntime().SagaStorage
            .ListSagaInstancesAsync(typeof(GiftLedgerSaga).FullName!, 10, TestContext.Current.CancellationToken);

        instances.Select(x => x.Identity.ToString()).ShouldBe(["g-1"]);
    }

    [Fact]
    public async Task reads_the_ancillary_stores_saga()
    {
        var state = await theHost.GetRuntime().SagaStorage
            .ReadSagaAsync(typeof(GiftLedgerSaga).FullName!, "g-1", TestContext.Current.CancellationToken);

        state.ShouldNotBeNull();
        state.Identity.ShouldBe("g-1");
    }

    [Fact]
    public async Task queries_the_ancillary_stores_saga()
    {
        var result = await theHost.GetRuntime().SagaStorage
            .QuerySagasAsync(typeof(GiftLedgerSaga).FullName!, new JasperFx.Documents.DocumentQueryOptions(1, 10),
                TestContext.Current.CancellationToken);

        result.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task still_reads_the_main_stores_saga()
    {
        var storage = theHost.GetRuntime().SagaStorage;

        (await storage.ReadSagaAsync(typeof(OrderLedgerSaga).FullName!, "o-1", TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        (await storage.ListSagaInstancesAsync(typeof(OrderLedgerSaga).FullName!, 10, TestContext.Current.CancellationToken))
            .Select(x => x.Identity.ToString()).ShouldBe(["o-1"]);
    }
}

public interface IGiftSagaStore : IDocumentStore;

public record StartOrderLedger(string Id);

public record StartGiftLedger(string Id);

public class OrderLedgerSaga : Wolverine.Saga
{
    public string Id { get; set; } = string.Empty;

    public static OrderLedgerSaga Start(StartOrderLedger command) => new() { Id = command.Id };
}

[MartenStore(typeof(IGiftSagaStore))]
public class GiftLedgerSaga : Wolverine.Saga
{
    public string Id { get; set; } = string.Empty;

    public static GiftLedgerSaga Start(StartGiftLedger command) => new() { Id = command.Id };
}
