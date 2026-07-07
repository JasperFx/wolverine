using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using Marten;
using Marten.Events;
using MartenTests.Distribution.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Distribution;
using Wolverine.MessagePack;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

// Phase 2 of the per-tenant-partitioned-events matrix (#3021): projection/subscription distribution
// granularity under a SINGLE database with Conjoined + Quick + UseTenantPartitionedEvents.
//
// FLIPPED for Marten 9.13.0-alpha.3 (marten#4862 / marten#4864): this test originally pinned the old
// behavior — agents distributed per (shard × DATABASE), one store-global "…/all" agent spanning every
// tenant partition, with "one agent per tenant" true only under sharded databases. That store-global
// agent's single progression floor silently skipped a lagging tenant's low per-tenant seq_ids (see
// single_db_tenant_partitioned_distribution for the regression shape). marten#4864 broadens
// IEventStore.DistributesAgentsPerTenant to ANY tenant-partitioned store and surfaces the managed
// tenants on the single-DB usage descriptor's TenantIds, so one async projection now fans out one
// agent PER TENANT ("…/all/{tenant}") even on a single database, each tracking its own per-tenant
// progression.
public class tenant_partitioned_distribution_granularity(ITestOutputHelper output) : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    // Unique schema per run: managed partitions created in one run otherwise break the next run's
    // resource-setup DDL reconciliation against the same schema.
    private readonly string theSchema = "csp_tpe_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();
                opts.UseMessagePackSerialization();

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = theSchema;

                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
                        m.Events.AppendMode = EventAppendMode.Quick;
                        m.Events.UseTenantPartitionedEvents = true;
                        m.Events.UseIdentityMapForAggregates = false;

                        m.Schema.For<PartitionedCounter>().MultiTenanted();
                        m.Projections.Snapshot<PartitionedCounter>(SnapshotLifecycle.Async);
                    })
                    .IntegrateWithWolverine(m => m.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await theStore.Advanced.AddMartenManagedTenantsAsync(default, new Dictionary<string, string>
        {
            ["tenant1"] = "tenant1",
            ["tenant2"] = "tenant2",
            [StorageConstants.DefaultTenantId] = "default"
        });
    }

    public async Task DisposeAsync()
    {
        theHost.GetRuntime().Agents.DisableHealthChecks();
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task one_async_projection_fans_out_one_agent_per_tenant()
    {
        await theHost.WaitUntilAssumesLeadershipAsync(10.Seconds());

        // marten#4862/#4864 (Marten 9.13.0-alpha.3): one async projection over a single
        // tenant-partitioned database fans out per tenant — three registered tenants
        // (tenant1/tenant2/*DEFAULT*) => exactly THREE subscription agents. This used to be ONE
        // store-global agent with no tenant component.
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theHost, 3);
        }, 60.Seconds());

        var uris = await GetAgentUrisAsync(theHost);
        uris.Length.ShouldBe(3);

        // Every agent Uri now carries the tenant in the shard path: "…/all/{tenant}".
        uris.ShouldAllBe(u => u.Contains("/all"));
        uris.ShouldContain(u => u.TrimEnd('/').EndsWith("/tenant1", StringComparison.OrdinalIgnoreCase));
        uris.ShouldContain(u => u.TrimEnd('/').EndsWith("/tenant2", StringComparison.OrdinalIgnoreCase));
        // ...and the default tenant gets its own agent too (it is a registered managed tenant).
        uris.ShouldContain(u => !u.TrimEnd('/').EndsWith("/tenant1", StringComparison.OrdinalIgnoreCase)
                                && !u.TrimEnd('/').EndsWith("/tenant2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task every_tenant_partition_projects_through_its_own_agent()
    {
        await theHost.WaitUntilAssumesLeadershipAsync(10.Seconds());
        // marten#4864: per-tenant fan-out — three registered tenants => three agents.
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theHost, 3);
        }, 60.Seconds());

        var id = "pc-" + Guid.NewGuid().ToString("N");
        await AppendAsync("tenant1", id, 5);
        await AppendAsync("tenant2", id, 11); // same stream id, different tenant partition

        await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());

        // Each tenant's own agent processed its partition into that tenant's own projection doc.
        (await LoadAsync("tenant1", id))!.Total.ShouldBe(5);
        (await LoadAsync("tenant2", id))!.Total.ShouldBe(11);
    }

    private async Task AppendAsync(string tenant, string id, int amount)
    {
        await using var session = theStore.LightweightSession(tenant);
        session.Events.StartStream<PartitionedCounter>(id, new CounterChanged(amount));
        await session.SaveChangesAsync();
    }

    private async Task<PartitionedCounter?> LoadAsync(string tenant, string id)
    {
        await using var session = theStore.LightweightSession(tenant);
        return await session.LoadAsync<PartitionedCounter>(id);
    }

    private static async Task<string[]> GetAgentUrisAsync(IHost host)
    {
        var family = host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();
        var agents = await family.AllKnownAgentsAsync();
        return [.. agents.Select(x => x.AbsoluteUri)];
    }
}

public record CounterChanged(int Amount);

public class PartitionedCounter
{
    public string Id { get; set; } = null!;
    public int Total { get; set; }
    public void Apply(CounterChanged e) => Total += e.Amount;
}
