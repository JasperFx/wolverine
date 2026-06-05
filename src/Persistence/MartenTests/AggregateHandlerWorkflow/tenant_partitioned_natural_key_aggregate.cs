using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.AggregateHandlerWorkflow;

// Phase 1 (cont., #3021): natural-key aggregate isolation under UseTenantPartitionedEvents. Reuses the
// NkOrderAggregate / NkHandlerOrderNumber / NkOrderHandler types from natural_key_aggregate_handler_workflow.cs.
// The aggregate keys streams by Guid but is fetched by a string natural key (NkHandlerOrderNumber) via
// FetchForWriting<T, TNaturalKey>; the same natural-key value in two tenants must resolve to that tenant's
// own stream (the natural-key lookup is scoped to the routed tenant partition).
public class tenant_partitioned_natural_key_aggregate : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        theHost = await PartitionedTenancyHost.StartAsync(StreamIdentity.AsGuid,
            "tpe_nk_" + Guid.NewGuid().ToString("N"),
            m =>
            {
                m.Schema.For<NkOrderAggregate>().MultiTenanted();
                m.Projections.Snapshot<NkOrderAggregate>(SnapshotLifecycle.Inline);
            },
            typeof(NkOrderHandler));

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    // Seed a fresh order stream in the given tenant via a direct tenant session.
    private async Task<Guid> SeedOrder(string tenant, string orderNo, string customer)
    {
        var streamId = Guid.NewGuid();
        await using var session = theStore.LightweightSession(tenant);
        session.Events.StartStream<NkOrderAggregate>(streamId,
            new NkHandlerOrderCreated(new NkHandlerOrderNumber(orderNo), customer));
        await session.SaveChangesAsync();
        return streamId;
    }

    private async Task<NkOrderAggregate?> Load(string tenant, Guid streamId)
    {
        await using var session = theStore.LightweightSession(tenant);
        return await session.LoadAsync<NkOrderAggregate>(streamId);
    }

    private static string UniqueOrderNo() => "ORD-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task same_natural_key_in_two_tenants_resolves_to_the_routed_tenant_stream()
    {
        var orderNo = UniqueOrderNo();
        var id1 = await SeedOrder("tenant1", orderNo, "Alice");
        var id2 = await SeedOrder("tenant2", orderNo, "Bob"); // same natural key, different tenant

        // Routed to tenant1: the natural-key fetch must find tenant1's order, not tenant2's.
        await theHost.InvokeMessageAndWaitAsync(
            new AddNkOrderItem(new NkHandlerOrderNumber(orderNo), "Widget", 9.99m), "tenant1");

        var t1 = await Load("tenant1", id1);
        t1!.CustomerName.ShouldBe("Alice");
        t1.TotalAmount.ShouldBe(9.99m);

        // tenant2's same-natural-key order is untouched.
        var t2 = await Load("tenant2", id2);
        t2!.CustomerName.ShouldBe("Bob");
        t2.TotalAmount.ShouldBe(0m);
    }

    [Fact]
    public async Task natural_key_multi_event_return_appends_to_the_routed_tenant()
    {
        var orderNo = UniqueOrderNo();
        var id1 = await SeedOrder("tenant1", orderNo, "Alice");
        await SeedOrder("tenant2", orderNo, "Bob");

        await theHost.InvokeMessageAndWaitAsync(
            new AddNkOrderItems(new NkHandlerOrderNumber(orderNo),
                [("Gadget", 19.99m), ("Doohickey", 5.50m)]), "tenant1");

        (await Load("tenant1", id1))!.TotalAmount.ShouldBe(25.49m);
    }

    [Fact]
    public async Task natural_key_event_stream_handler_completes_in_the_routed_tenant()
    {
        // CompleteNkOrder's handler appends via a [WriteAggregate] IEventStream<NkOrderAggregate> — the
        // aggregate-workflow path (which applies transaction support, unlike the loader path in #3032).
        var orderNo = UniqueOrderNo();
        var id1 = await SeedOrder("tenant1", orderNo, "Alice");
        var id2 = await SeedOrder("tenant2", orderNo, "Bob");

        await theHost.InvokeMessageAndWaitAsync(
            new CompleteNkOrder(new NkHandlerOrderNumber(orderNo)), "tenant1");

        (await Load("tenant1", id1))!.IsComplete.ShouldBeTrue();
        (await Load("tenant2", id2))!.IsComplete.ShouldBeFalse(); // isolated
    }
}
