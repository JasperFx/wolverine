using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

// Foundational slice of GH-3018: exercise the Wolverine aggregate-handler workflow against a Marten
// event store using per-tenant event partitioning (Events.UseTenantPartitionedEvents) — Conjoined
// tenancy + Quick append, on both string and guid stream identity.
//
// Load-bearing facts pinned here:
//   * Tenant is the partition selector — a handler command routed to a tenant appends to that
//     tenant's partition; the same stream-id value in two tenants stays isolated.
//   * A command with no tenant falls to the default-tenant partition, isolated from the others.
//   * Marten 9.5.2 uses MANAGED partitions: a tenant must be registered via
//     AddMartenManagedTenantsAsync before its first append, otherwise MT002 (this is NOT the lazy
//     42P01 provisioning the issue originally assumed — see the PR notes / issue checklist).
internal static class PartitionedTenancyHost
{
    public static async Task<IHost> StartAsync(StreamIdentity identity, string schema, Action<StoreOptions> configureAggregate, params Type[] handlers)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = schema;

                        m.Events.StreamIdentity = identity;
                        m.Events.TenancyStyle = TenancyStyle.Conjoined; // required by UseTenantPartitionedEvents
                        m.Events.AppendMode = EventAppendMode.Quick;    // QuickAppend-only
                        m.Events.UseTenantPartitionedEvents = true;
                        m.Events.UseIdentityMapForAggregates = false;

                        configureAggregate(m);
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handler in handlers) discovery.IncludeType(handler);

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // Managed partitions — register the known tenants (and the default tenant, with a
        // table-safe suffix since "*DEFAULT*" is not a legal partition suffix) before any append.
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.AddMartenManagedTenantsAsync(default, new Dictionary<string, string>
        {
            ["tenant1"] = "tenant1",
            ["tenant2"] = "tenant2",
            [StorageConstants.DefaultTenantId] = "default"
        });

        return host;
    }
}

public class tenant_partitioned_events_string_identity : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        // Unique schema per run: managed partitions created in one run otherwise break the next
        // run's resource-setup DDL reconciliation against the same schema.
        theHost = await PartitionedTenancyHost.StartAsync(StreamIdentity.AsString, "tpe_str_" + Guid.NewGuid().ToString("N"),
            m =>
            {
                m.Schema.For<TenantTally>().MultiTenanted();
                m.Projections.Snapshot<TenantTally>(SnapshotLifecycle.Inline);
            }, typeof(TenantTallyHandler));

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<TenantTally?> LoadAsync(string tenantId, string id)
    {
        await using var session = theStore.LightweightSession(tenantId);
        return await session.LoadAsync<TenantTally>(id);
    }

    // Unique per run — the shared schema is not reset between runs, so fixed ids would accumulate.
    private static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task append_lands_in_the_routed_tenant_partition()
    {
        var id = UniqueId("tally");
        await theHost.InvokeMessageAndWaitAsync(new StartTally(id), "tenant1");
        await theHost.InvokeMessageAndWaitAsync(new IncrementTally(id, 5), "tenant1");
        await theHost.InvokeMessageAndWaitAsync(new IncrementTally(id, 2), "tenant1");

        (await LoadAsync("tenant1", id))!.Total.ShouldBe(7);
        // Not visible in another tenant's partition.
        (await LoadAsync("tenant2", id)).ShouldBeNull();
    }

    [Fact]
    public async Task same_stream_id_in_two_tenants_stays_isolated()
    {
        var id = UniqueId("shared");
        await theHost.InvokeMessageAndWaitAsync(new StartTally(id), "tenant1");
        await theHost.InvokeMessageAndWaitAsync(new IncrementTally(id, 5), "tenant1");

        await theHost.InvokeMessageAndWaitAsync(new StartTally(id), "tenant2");
        await theHost.InvokeMessageAndWaitAsync(new IncrementTally(id, 100), "tenant2");

        (await LoadAsync("tenant1", id))!.Total.ShouldBe(5);
        (await LoadAsync("tenant2", id))!.Total.ShouldBe(100);
    }

    [Fact]
    public async Task no_tenant_falls_to_the_default_partition_isolated()
    {
        var id = UniqueId("def");
        await theHost.InvokeMessageAndWaitAsync(new StartTally(id));
        await theHost.InvokeMessageAndWaitAsync(new IncrementTally(id, 9));

        // Landed in the default-tenant partition...
        await using (var session = theStore.LightweightSession())
        {
            (await session.LoadAsync<TenantTally>(id))!.Total.ShouldBe(9);
        }

        // ...and is invisible to the named-tenant partitions.
        (await LoadAsync("tenant1", id)).ShouldBeNull();
        (await LoadAsync("tenant2", id)).ShouldBeNull();
    }
}

public class tenant_partitioned_events_guid_identity : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        theHost = await PartitionedTenancyHost.StartAsync(StreamIdentity.AsGuid, "tpe_guid_" + Guid.NewGuid().ToString("N"),
            m =>
            {
                m.Schema.For<GuidTally>().MultiTenanted();
                m.Projections.Snapshot<GuidTally>(SnapshotLifecycle.Inline);
            }, typeof(GuidTallyHandler));

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<GuidTally?> LoadAsync(string tenantId, Guid id)
    {
        await using var session = theStore.LightweightSession(tenantId);
        return await session.LoadAsync<GuidTally>(id);
    }

    [Fact]
    public async Task append_lands_in_the_routed_tenant_partition()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new StartGuidTally(id), "tenant1");
        await theHost.InvokeMessageAndWaitAsync(new IncrementGuidTally(id, 3), "tenant1");

        (await LoadAsync("tenant1", id))!.Total.ShouldBe(3);
        (await LoadAsync("tenant2", id)).ShouldBeNull();
    }

    [Fact]
    public async Task same_stream_id_in_two_tenants_stays_isolated()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new StartGuidTally(id), "tenant1");
        await theHost.InvokeMessageAndWaitAsync(new IncrementGuidTally(id, 5), "tenant1");

        await theHost.InvokeMessageAndWaitAsync(new StartGuidTally(id), "tenant2");
        await theHost.InvokeMessageAndWaitAsync(new IncrementGuidTally(id, 50), "tenant2");

        (await LoadAsync("tenant1", id))!.Total.ShouldBe(5);
        (await LoadAsync("tenant2", id))!.Total.ShouldBe(50);
    }
}

public record TallyIncremented(int Amount);

public class TenantTally
{
    public string Id { get; set; } = null!;
    public int Total { get; set; }
    public void Apply(TallyIncremented e) => Total += e.Amount;
}

public record StartTally(string TenantTallyId);

public record IncrementTally(string TenantTallyId, int Amount);

public static class TenantTallyHandler
{
    public static IMartenOp Handle(StartTally command)
        => MartenOps.StartStream<TenantTally>(command.TenantTallyId, new TallyIncremented(0));

    [AggregateHandler]
    public static IEnumerable<object> Handle(IncrementTally command, TenantTally tally)
    {
        yield return new TallyIncremented(command.Amount);
    }
}

public class GuidTally
{
    public Guid Id { get; set; }
    public int Total { get; set; }
    public void Apply(TallyIncremented e) => Total += e.Amount;
}

public record StartGuidTally(Guid GuidTallyId);

public record IncrementGuidTally(Guid GuidTallyId, int Amount);

public static class GuidTallyHandler
{
    public static IMartenOp Handle(StartGuidTally command)
        => MartenOps.StartStream<GuidTally>(command.GuidTallyId, new TallyIncremented(0));

    [AggregateHandler]
    public static IEnumerable<object> Handle(IncrementGuidTally command, GuidTally tally)
    {
        yield return new TallyIncremented(command.Amount);
    }
}
