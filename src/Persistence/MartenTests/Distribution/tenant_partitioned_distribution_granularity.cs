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
// The load-bearing caveat this pins: Wolverine-managed event-subscription agents are distributed per
// (shard × DATABASE), NOT per tenant. With per-tenant *partitioning* the tenants all live in one
// database, so a single async projection yields exactly ONE agent ("…/all") spanning every tenant
// partition — not one agent per tenant. ("One agent per tenant" is only true under sharded databases,
// which is Phase 3.) The single shard still processes every tenant's events into that tenant's own
// projection document.
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
    public async Task one_async_projection_yields_one_agent_per_shard_not_per_tenant()
    {
        await theHost.WaitUntilAssumesLeadershipAsync(10.Seconds());

        // One async projection over a single database => exactly ONE subscription agent, even though
        // three tenants (tenant1/tenant2/*DEFAULT*) are registered. Per-tenant would be three.
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theHost, 1);
        }, 60.Seconds());

        var uris = await GetAgentUrisAsync(theHost);
        uris.Length.ShouldBe(1);

        // The agent Uri is keyed by (store/database/shard) — no tenant component.
        var uri = uris.Single();
        uri.ShouldContain("/all");
        uri.ShouldNotContain("tenant1");
        uri.ShouldNotContain("tenant2");
    }

    [Fact]
    public async Task the_single_shard_projects_every_tenant_partition()
    {
        await theHost.WaitUntilAssumesLeadershipAsync(10.Seconds());
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theHost, 1);
        }, 60.Seconds());

        var id = "pc-" + Guid.NewGuid().ToString("N");
        await AppendAsync("tenant1", id, 5);
        await AppendAsync("tenant2", id, 11); // same stream id, different tenant partition

        await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());

        // The one shard processed both tenant partitions into each tenant's own projection doc.
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
