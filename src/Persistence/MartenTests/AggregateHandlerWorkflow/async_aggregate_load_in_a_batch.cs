using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

/// <summary>
/// Loads of an Async aggregate share Wolverine's batched query with other loads, in any order and beside an
/// exclusive load.
/// </summary>
public class async_aggregate_load_in_a_batch : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async_aggregate_load_in_a_batch(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "async_in_batch";
                    m.Projections.Snapshot<NkProduct>(SnapshotLifecycle.Inline);
                    m.Projections.Snapshot<NkProductStock>(SnapshotLifecycle.Async);
                    m.Projections.Snapshot<AsyncTally>(SnapshotLifecycle.Async);
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(AsyncInBatchHandler));
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Services.AddResourceSetupOnStartup();
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await _store.Advanced.Clean.DeleteAllEventDataAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private string sourceFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        var code = _host.GetRuntime().Handlers.ChainFor<T>()!.SourceCode!;
        _output.WriteLine(code);
        return code;
    }

    private async Task<Guid> startTally()
    {
        var tallyId = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        session.Events.StartStream<AsyncTally>(tallyId, new TallyBumped());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tallyId;
    }

    private async Task<Guid> listProduct(NkSku sku, int stock)
    {
        var streamId = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        session.Events.StartStream<NkProduct>(streamId, new NkProductListed(sku, "Widget"), new NkProductRestocked(stock));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return streamId;
    }

    private async Task<int> tallyCount(Guid tallyId)
    {
        await using var query = _store.LightweightSession();
        return (await query.Events.FetchLatest<AsyncTally>(tallyId, TestContext.Current.CancellationToken))!.Count;
    }

    [Fact]
    public async Task an_async_aggregate_by_stream_id_after_another_batched_load()
    {
        var tallyId = await startTally();

        sourceFor<BumpAsyncTally>()
            .ShouldContain($"batchedQuery.Events.FetchForWriting<{typeof(AsyncTally).FullNameInCode()}>(");

        await _host.InvokeMessageAndWaitAsync(new BumpAsyncTally(tallyId));

        (await tallyCount(tallyId)).ShouldBe(2);
    }

    [Fact]
    public async Task an_async_aggregate_by_natural_key_after_another_batched_load()
    {
        var sku = new NkSku("ASYNC-IN-BATCH");
        var streamId = await listProduct(sku, 1);

        await _host.InvokeMessageAndWaitAsync(new RestockNkProductStockAfterListing(sku, 2));

        await using var query = _store.QuerySession();
        (await query.LoadAsync<NkProduct>(streamId, TestContext.Current.CancellationToken))!.Stock.ShouldBe(3);
    }

    [Fact]
    public async Task an_async_read_after_another_batched_load()
    {
        var tallyId = await startTally();

        AsyncInBatchHandler.LastTally = null;
        await _host.InvokeMessageAndWaitAsync(new ViewAsyncTally(tallyId));

        AsyncInBatchHandler.LastTally!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task an_async_aggregate_beside_an_exclusive_load()
    {
        var tallyId = await startTally();
        var sku = new NkSku("ASYNC-AND-EXCLUSIVE");
        var productId = await listProduct(sku, 1);

        var code = sourceFor<BumpTallyAndRestockExclusively>();
        code.ShouldContain($"batchedQuery.Events.FetchForWriting<{typeof(AsyncTally).FullNameInCode()}>(");
        code.ShouldContain("batchedQuery.Events.FetchForExclusiveWriting<");

        await _host.InvokeMessageAndWaitAsync(new BumpTallyAndRestockExclusively(tallyId, sku, 4));

        (await tallyCount(tallyId)).ShouldBe(2);
        await using var query = _store.QuerySession();
        (await query.LoadAsync<NkProduct>(productId, TestContext.Current.CancellationToken))!.Stock.ShouldBe(5);
    }

    [Fact]
    public async Task an_exclusive_load_of_an_async_aggregate()
    {
        var tallyId = await startTally();

        sourceFor<BumpAsyncTallyExclusively>().ShouldContain("batchedQuery.Events.FetchForExclusiveWriting<");

        await _host.InvokeMessageAndWaitAsync(new BumpAsyncTallyExclusively(tallyId));

        (await tallyCount(tallyId)).ShouldBe(2);
    }
}

public record TallyBumped;

public class AsyncTally
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public static AsyncTally Create(TallyBumped e) => new() { Count = 1 };

    public void Apply(TallyBumped e) => Count++;
}

public record BumpAsyncTally(Guid AsyncTallyId);

public record BumpAsyncTallyExclusively(Guid AsyncTallyId);

public record ViewAsyncTally(Guid AsyncTallyId);

public record RestockNkProductStockAfterListing(NkSku Sku, int Quantity);

public record BumpTallyAndRestockExclusively(Guid AsyncTallyId, NkSku Sku, int Quantity);

public static class AsyncInBatchHandler
{
    public static AsyncTally? LastTally;

    // The Async aggregate load comes after another batched load
    public static TallyBumped Handle(BumpAsyncTally command, [All] IReadOnlyList<NkProduct> products,
        [WriteAggregate] AsyncTally tally) => new();

    public static NkProductRestocked Handle(RestockNkProductStockAfterListing command,
        [All] IReadOnlyList<NkProduct> products, [WriteAggregate] NkProductStock stock) => new(command.Quantity);

    public static void Handle(ViewAsyncTally query, [All] IReadOnlyList<NkProduct> products,
        [ReadAggregate] AsyncTally tally) => LastTally = tally;

    public static TallyBumped Handle(BumpAsyncTallyExclusively command, [All] IReadOnlyList<NkProduct> products,
        [WriteAggregate(LoadStyle = Wolverine.Marten.ConcurrencyStyle.Exclusive)] AsyncTally tally) => new();

    public static void Handle(BumpTallyAndRestockExclusively command,
        [WriteAggregate<NkSku>(LoadStyle = Wolverine.Marten.ConcurrencyStyle.Exclusive)] IEventStream<NkProduct> product,
        [All] IReadOnlyList<NkProduct> products,
        [WriteAggregate] IEventStream<AsyncTally> tally)
    {
        product.AppendOne(new NkProductRestocked(command.Quantity));
        tally.AppendOne(new TallyBumped());
    }
}
