using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

/// <summary>
///     GH-4439. An aggregate whose stream identity is a <see cref="NaturalKeyAttribute" /> and that is
///     registered ONLY on an ancillary store has to resolve that natural key through the store the chain is
///     routed to, not through the default <c>IDocumentStore</c>.
/// </summary>
/// <remarks>
///     <para>
///     Natural-key detection was the one step of the aggregate handler workflow that still asked the default
///     store by name: <c>MartenEventSourcingFrameProvider.TryDetermineNaturalKeyType</c> read
///     <c>container.GetInstance&lt;IDocumentStore&gt;().Options</c>, and Marten's
///     <c>FindNaturalKeyDefinition</c> is per store (it searches that store's registered projections). For an
///     aggregate that lives only on an ancillary store it therefore returned null, the natural-key branch was
///     never taken, and codegen failed with "Unable to determine an aggregate id for the parameter ...". The
///     same handler on the default store generated <c>FetchForWriting&lt;T, TKey&gt;</c> and worked, which is
///     what makes this a routing bug rather than a natural-key one.
///     </para>
///     <para>
///     Both spellings are covered because they reach the same seam: <c>[WriteAggregate]</c> names the Marten
///     provider directly, <c>[WriteModel]</c> resolves it through the registered persistence strategies
///     (Marten's <c>CanPersist</c> is catch-all), and neither knew which store the chain was routed to.
///     </para>
/// </remarks>
public class natural_key_aggregate_on_ancillary_store : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // The main store knows NOTHING about the aggregate. That is the whole point: if natural-key
                // detection asks this store, it finds nothing and the workflow gives up.
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "nk_ancillary_main";
                    m.Events.DatabaseSchemaName = "nk_ancillary_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<INaturalKeyOrderStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "nk_ancillary_orders";
                    m.Events.DatabaseSchemaName = "nk_ancillary_orders";
                    m.Projections.Snapshot<AncillaryNkOrder>(SnapshotLifecycle.Inline);
                }).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryNkOrderHandler));

                opts.Services.AddResourceSetupOnStartup();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        // Both tests seed a hardcoded natural key against a brand new stream id, and the
        // mt_natural_key_ancillarynkorder lookup table outlives the run. Without this reset a second run
        // against the same database fails with DuplicateNaturalKeyException, which reads exactly like a
        // natural-key regression in the store.
        await theHost.DocumentStore<INaturalKeyOrderStore>().Advanced.ResetAllData();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task write_aggregate_resolves_the_natural_key_through_the_ancillary_store()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new AncillaryOrderNumber("ORD-ANC-001");

        var store = theHost.DocumentStore<INaturalKeyOrderStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<AncillaryNkOrder>(streamId, new AncillaryNkOrderCreated(orderNumber));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new AddAncillaryNkItem(orderNumber, 12.50m));

        await using var verify = store.LightweightSession();
        var order = await verify.LoadAsync<AncillaryNkOrder>(streamId, TestContext.Current.CancellationToken);

        order.ShouldNotBeNull();
        order.TotalAmount.ShouldBe(12.50m);

        // ...and the main store never saw the stream. A fetch that silently fell back to the default store
        // would have appended there instead, and the positive assertion alone cannot tell the difference.
        var mainStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await using var mainSession = mainStore.LightweightSession();
        var mainEvents = await mainSession.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken);
        mainEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task write_model_resolves_the_natural_key_through_the_ancillary_store()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new AncillaryOrderNumber("ORD-ANC-002");

        var store = theHost.DocumentStore<INaturalKeyOrderStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<AncillaryNkOrder>(streamId, new AncillaryNkOrderCreated(orderNumber));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new CompleteAncillaryNkOrder(orderNumber));

        await using var verify = store.LightweightSession();
        var order = await verify.LoadAsync<AncillaryNkOrder>(streamId, TestContext.Current.CancellationToken);

        order.ShouldNotBeNull();
        order.IsComplete.ShouldBeTrue();
    }
}

public interface INaturalKeyOrderStore : IDocumentStore;

public record AncillaryOrderNumber(string Value);

public class AncillaryNkOrder
{
    public Guid Id { get; set; }

    [NaturalKey]
    public AncillaryOrderNumber OrderNum { get; set; } = null!;

    public decimal TotalAmount { get; set; }
    public bool IsComplete { get; set; }

    [NaturalKeySource]
    public void Apply(AncillaryNkOrderCreated e)
    {
        OrderNum = e.OrderNumber;
    }

    public void Apply(AncillaryNkItemAdded e)
    {
        TotalAmount += e.Price;
    }

    public void Apply(AncillaryNkOrderCompleted e)
    {
        IsComplete = true;
    }
}

public record AncillaryNkOrderCreated(AncillaryOrderNumber OrderNumber);
public record AncillaryNkItemAdded(decimal Price);
public record AncillaryNkOrderCompleted;

public record AddAncillaryNkItem(AncillaryOrderNumber OrderNum, decimal Price);
public record CompleteAncillaryNkOrder(AncillaryOrderNumber OrderNum);

[MartenStore(typeof(INaturalKeyOrderStore))]
public static class AncillaryNkOrderHandler
{
    public static AncillaryNkItemAdded Handle(AddAncillaryNkItem command,
        [WriteAggregate] AncillaryNkOrder order)
    {
        return new AncillaryNkItemAdded(command.Price);
    }

    // The store-agnostic spelling of the same thing, which resolves its provider through the registered
    // persistence strategies rather than naming Marten.
    public static AncillaryNkOrderCompleted Handle(CompleteAncillaryNkOrder command,
        [Wolverine.Persistence.EventSourcing.WriteModel] AncillaryNkOrder order)
    {
        return new AncillaryNkOrderCompleted();
    }
}
