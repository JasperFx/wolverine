using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

// Phase 1 of the per-tenant-partitioned-events matrix (follow-up #3021): the single-store
// aggregate-handler scenarios beyond the foundational slice — [ReadAggregate], every append
// return shape, and [WriteAggregate] — each scoped by tenant against a Conjoined + Quick +
// UseTenantPartitionedEvents store (string identity). Reuses TenantTally / PartitionedTenancyHost
// from tenant_partitioned_events_aggregate_workflow.cs.
public class tenant_partitioned_aggregate_matrix : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        theHost = await PartitionedTenancyHost.StartAsync(StreamIdentity.AsString,
            "tpe_matrix_" + Guid.NewGuid().ToString("N"),
            m =>
            {
                m.Schema.For<TenantTally>().MultiTenanted();
                m.Projections.Snapshot<TenantTally>(SnapshotLifecycle.Inline);
            },
            typeof(TenantTallyHandler), typeof(PhaseOneMatrixHandlers));

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private Task SeedAsync(string tenant, string id)
        => theHost.InvokeMessageAndWaitAsync(new StartTally(id), tenant);

    private async Task<TenantTally?> LoadAsync(string tenant, string id)
    {
        await using var session = theStore.LightweightSession(tenant);
        return await session.LoadAsync<TenantTally>(id);
    }

    private static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task read_aggregate_reads_the_routed_tenant_partition()
    {
        var id = UniqueId("read");
        await SeedAsync("tenant1", id);
        await theHost.InvokeMessageAndWaitAsync(new IncrementTally(id, 4), "tenant1");

        // Reads tenant1's partition.
        var (_, view1) = await theHost.InvokeMessageAndWaitAsync<TallyView>(new ViewTally(id), "tenant1");
        view1!.Total.ShouldBe(4);

        // Required=false on a tenant with no such stream -> null aggregate -> sentinel.
        var (_, view2) = await theHost.InvokeMessageAndWaitAsync<TallyView>(new ViewTally(id), "tenant2");
        view2!.Total.ShouldBe(-1);
    }

    [Fact]
    public async Task single_event_return_appends_to_the_tenant_partition()
    {
        var id = UniqueId("one");
        await SeedAsync("tenant1", id);
        await theHost.InvokeMessageAndWaitAsync(new IncrementOne(id, 3), "tenant1");

        (await LoadAsync("tenant1", id))!.Total.ShouldBe(3);
        (await LoadAsync("tenant2", id)).ShouldBeNull();
    }

    [Fact]
    public async Task enumerable_return_appends_to_the_tenant_partition()
    {
        var id = UniqueId("many");
        await SeedAsync("tenant1", id);
        await theHost.InvokeMessageAndWaitAsync(new IncrementSeveral(id, new[] { 1, 2, 3 }), "tenant1");

        (await LoadAsync("tenant1", id))!.Total.ShouldBe(6);
        (await LoadAsync("tenant2", id)).ShouldBeNull();
    }

    [Fact]
    public async Task events_return_appends_to_the_tenant_partition()
    {
        var id = UniqueId("events");
        await SeedAsync("tenant1", id);
        await theHost.InvokeMessageAndWaitAsync(new IncrementViaEvents(id, 7), "tenant1");

        (await LoadAsync("tenant1", id))!.Total.ShouldBe(7);
        (await LoadAsync("tenant2", id)).ShouldBeNull();
    }

    [Fact]
    public async Task async_enumerable_return_appends_to_the_tenant_partition()
    {
        var id = UniqueId("async");
        await SeedAsync("tenant1", id);
        await theHost.InvokeMessageAndWaitAsync(new IncrementViaAsync(id, 9), "tenant1");

        (await LoadAsync("tenant1", id))!.Total.ShouldBe(9);
        (await LoadAsync("tenant2", id)).ShouldBeNull();
    }

    [Fact]
    public async Task write_aggregate_appends_to_the_routed_tenant_and_stays_isolated()
    {
        var id = UniqueId("write");
        await SeedAsync("tenant1", id);

        await theHost.InvokeMessageAndWaitAsync(new AppendToTally(id, 11), "tenant1");

        (await LoadAsync("tenant1", id))!.Total.ShouldBe(11);
        // The same stream id, routed to tenant2, sees no aggregate (separate partition).
        (await LoadAsync("tenant2", id)).ShouldBeNull();
    }
}

public record ViewTally(string TenantTallyId);

public record TallyView(int Total);

public record IncrementOne(string TenantTallyId, int Amount);

public record IncrementSeveral(string TenantTallyId, int[] Amounts);

public record IncrementViaEvents(string TenantTallyId, int Amount);

public record IncrementViaAsync(string TenantTallyId, int Amount);

public record AppendToTally(string TenantTallyId, int Amount);

public static class PhaseOneMatrixHandlers
{
    public static TallyView Handle(ViewTally command, [ReadAggregate(Required = false)] TenantTally? tally)
        => new(tally?.Total ?? -1);

    [AggregateHandler]
    public static object Handle(IncrementOne command, TenantTally tally)
        => new TallyIncremented(command.Amount);

    [AggregateHandler]
    public static IEnumerable<object> Handle(IncrementSeveral command, TenantTally tally)
        => command.Amounts.Select(a => (object)new TallyIncremented(a));

    [AggregateHandler]
    public static Events Handle(IncrementViaEvents command, TenantTally tally)
        => new Events(new object[] { new TallyIncremented(command.Amount) });

    [AggregateHandler]
    public static async IAsyncEnumerable<object> Handle(IncrementViaAsync command, TenantTally tally)
    {
        await Task.CompletedTask;
        yield return new TallyIncremented(command.Amount);
    }

    public static Events Handle(AppendToTally command, [WriteAggregate(Required = false)] TenantTally? tally)
        => new Events(new object[] { new TallyIncremented(command.Amount) });
}
