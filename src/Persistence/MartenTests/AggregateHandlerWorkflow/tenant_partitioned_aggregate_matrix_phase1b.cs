using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

// Phase 1 (cont., follow-up #3021): the remaining single-store aggregate-handler matrix that reuses
// the Conjoined + Quick + UseTenantPartitionedEvents fixture (string identity) — exclusive-write
// concurrency, empty-result→no-write, IEventStream<T> handler parameter, MartenOps tenant document
// overloads, DeliveryOptions{TenantId} routing, and AlwaysEnforceConsistency. Each scenario asserts
// the work lands in (and stays isolated to) the routed tenant partition.
public class tenant_partitioned_aggregate_matrix_phase1b : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        theHost = await PartitionedTenancyHost.StartAsync(StreamIdentity.AsString,
            "tpe_p1b_" + Guid.NewGuid().ToString("N"),
            m =>
            {
                m.Schema.For<TenantTally>().MultiTenanted();
                m.Projections.Snapshot<TenantTally>(SnapshotLifecycle.Inline);
                m.Schema.For<TenantLedger>().MultiTenanted();
            },
            typeof(Phase1bHandlers), typeof(TenantLedgerHandler), typeof(ConsistentTallyHandler));

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task SeedTally(string tenant, string id)
    {
        await using var session = theStore.LightweightSession(tenant);
        session.Events.StartStream<TenantTally>(id, new TallyIncremented(0));
        await session.SaveChangesAsync();
    }

    private async Task<TenantTally?> LoadTally(string tenant, string id)
    {
        await using var session = theStore.LightweightSession(tenant);
        return await session.LoadAsync<TenantTally>(id);
    }

    private static string UniqueId(string p) => $"{p}-{Guid.NewGuid():N}";

    [Fact]
    public async Task exclusive_write_appends_to_the_routed_tenant_and_stays_isolated()
    {
        var id = UniqueId("excl");
        await SeedTally("tenant1", id);
        await SeedTally("tenant2", id);

        await theHost.InvokeMessageAndWaitAsync(new ExclusiveIncrement(id, 8), "tenant1");

        (await LoadTally("tenant1", id))!.Total.ShouldBe(8);
        (await LoadTally("tenant2", id))!.Total.ShouldBe(0);
    }

    [Fact]
    public async Task empty_result_makes_no_write()
    {
        var id = UniqueId("noop");
        await SeedTally("tenant1", id);

        await theHost.InvokeMessageAndWaitAsync(new NoOpTally(id), "tenant1");

        // Still just the seed event — no new append, aggregate unchanged.
        await using var session = theStore.LightweightSession("tenant1");
        (await session.Events.FetchStreamAsync(id)).Count.ShouldBe(1);
        (await LoadTally("tenant1", id))!.Total.ShouldBe(0);
    }

    // NOTE: the IEventStream<T> handler-parameter return shape is deliberately NOT covered here. A
    // compound handler that loads an IEventStream<T> via FetchForWriting and appends to it does not
    // get Marten transaction support (no SaveChanges) without opts.Policies.AutoApplyTransactions() —
    // so the append is silently dropped, unlike [AggregateHandler]/IMartenOp returns (the latter
    // self-persist since #3025). This fixture intentionally runs without AutoApplyTransactions; the
    // gap is filed as #3032. Bug_225 pins the working path (AutoApplyTransactions on).

    [Fact]
    public async Task martenops_store_tenant_overload_lands_in_that_tenant_partition()
    {
        // Invoked with NO ambient tenant — the op's tenant overload selects the partition.
        var id = UniqueId("led");
        await theHost.InvokeMessageAndWaitAsync(new StoreLedgerForTenant(id, "tenant1", 42));

        (await LoadLedger("tenant1", id))!.Value.ShouldBe(42);
        (await LoadLedger("tenant2", id)).ShouldBeNull();
        (await LoadLedger(StorageConstants.DefaultTenantId, id)).ShouldBeNull();
    }

    [Fact]
    public async Task martenops_insert_then_delete_tenant_overloads_target_that_tenant()
    {
        var id = UniqueId("led2");
        await theHost.InvokeMessageAndWaitAsync(new InsertLedgerForTenant(id, "tenant2", 7));
        (await LoadLedger("tenant2", id))!.Value.ShouldBe(7);
        (await LoadLedger("tenant1", id)).ShouldBeNull();

        await theHost.InvokeMessageAndWaitAsync(new DeleteLedgerForTenant(id, "tenant2"));
        (await LoadLedger("tenant2", id)).ShouldBeNull();
    }

    [Fact]
    public async Task delivery_options_tenant_id_routes_to_that_partition()
    {
        var id = UniqueId("deliv");
        await SeedTally("tenant1", id);

        await theHost.TrackActivity().ExecuteAndWaitAsync(c =>
            c.PublishAsync(new DeliveryRoutedIncrement(id, 5), new DeliveryOptions { TenantId = "tenant1" }));

        (await LoadTally("tenant1", id))!.Total.ShouldBe(5);
        (await LoadTally("tenant2", id)).ShouldBeNull();
    }

    [Fact]
    public async Task always_enforce_consistency_detects_concurrent_write_in_the_tenant_partition()
    {
        var id = UniqueId("aec");
        await SeedTally("tenant1", id);

        // The handler emits no events but a concurrent writer advances the same (tenant1) stream
        // mid-flight; AlwaysEnforceConsistency must still raise on SaveChanges, scoped to the partition.
        await Should.ThrowAsync<ConcurrencyException>(() =>
            theHost.MessageBus().InvokeForTenantAsync("tenant1", new ConsistentNoOpTally(id)));
    }

    private async Task<TenantLedger?> LoadLedger(string tenant, string id)
    {
        await using var session = theStore.LightweightSession(tenant);
        return await session.LoadAsync<TenantLedger>(id);
    }
}

public record ExclusiveIncrement(string TenantTallyId, int Amount);

public record NoOpTally(string TenantTallyId);

public record DeliveryRoutedIncrement(string TenantTallyId, int Amount);

public static class Phase1bHandlers
{
    [AggregateHandler(Wolverine.Marten.ConcurrencyStyle.Exclusive)]
    public static IEnumerable<object> Handle(ExclusiveIncrement command, TenantTally tally)
    {
        yield return new TallyIncremented(command.Amount);
    }

    [AggregateHandler]
    public static IEnumerable<object> Handle(NoOpTally command, TenantTally tally)
    {
        yield break;
    }

    [AggregateHandler]
    public static IEnumerable<object> Handle(DeliveryRoutedIncrement command, TenantTally tally)
    {
        yield return new TallyIncremented(command.Amount);
    }
}

public class TenantLedger
{
    public string Id { get; set; } = null!;
    public int Value { get; set; }
}

public record StoreLedgerForTenant(string Id, string Tenant, int Value);

public record InsertLedgerForTenant(string Id, string Tenant, int Value);

public record DeleteLedgerForTenant(string Id, string Tenant);

public static class TenantLedgerHandler
{
    public static IMartenOp Handle(StoreLedgerForTenant command)
        => MartenOps.Store(new TenantLedger { Id = command.Id, Value = command.Value }, command.Tenant);

    public static IMartenOp Handle(InsertLedgerForTenant command)
        => MartenOps.Insert(new TenantLedger { Id = command.Id, Value = command.Value }, command.Tenant);

    public static IMartenOp Handle(DeleteLedgerForTenant command)
        => MartenOps.Delete(new TenantLedger { Id = command.Id }, command.Tenant);
}

public record ConsistentNoOpTally(string TenantTallyId);

public static class ConsistentTallyHandler
{
    [AggregateHandler(AlwaysEnforceConsistency = true)]
    public static async Task<IEnumerable<object>> Handle(
        ConsistentNoOpTally command, TenantTally tally, IDocumentStore store, Envelope envelope)
    {
        // Simulate a concurrent writer on the same (tenant) stream between FetchForWriting and SaveChanges.
        await using var session = store.LightweightSession(envelope.TenantId!);
        session.Events.Append(command.TenantTallyId, new TallyIncremented(1));
        await session.SaveChangesAsync();

        return Array.Empty<object>();
    }
}
