using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MartenTests.Distribution.Support;
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

// Phase 2 of #3021 (multi-node slice): the clustered case of the granularity caveat. Two async
// projections on a SINGLE database with Conjoined + Quick + UseTenantPartitionedEvents and three
// managed tenants. Across a two-node cluster the subscription agents distribute per (shard × DATABASE)
// — two agents total, one per node — NOT per tenant (which would be 2 × 3 = 6). Confirms Wolverine-
// managed distribution behaves identically under per-tenant partitioning as for a non-partitioned store.
//
// This was previously blocked by GH-3037 (a 2nd node's resource-setup threw 42P07 "relation
// mt_streams_default already exists"), root-caused to Weasel's name-based default-partition
// classification and fixed in Weasel 9.0.3 (weasel#300).
public class tenant_partitioned_distribution_multinode(ITestOutputHelper output) : PostgresqlContext, IAsyncLifetime
{
    private readonly ConcurrentBag<IHost> _hosts = [];
    private readonly string theSchema = "csp_mn_" + Guid.NewGuid().ToString("N");
    private IHost theOriginalHost = null!;

    public async Task InitializeAsync()
    {
        theOriginalHost = await StartHostAsync();

        // Managed partitions are stored in the shared database, so registering once (via the first
        // node) is visible to every node that joins the cluster on the same schema.
        var store = theOriginalHost.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.AddMartenManagedTenantsAsync(default, new Dictionary<string, string>
        {
            ["tenant1"] = "tenant1",
            ["tenant2"] = "tenant2",
            [StorageConstants.DefaultTenantId] = "default"
        });
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.GetRuntime().Agents.DisableHealthChecks();
            await host.StopAsync();
            host.Dispose();
        }
    }

    private async Task<IHost> StartHostAsync()
    {
        var host = await Host.CreateDefaultBuilder()
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

                        m.Schema.For<NodeCounterA>().MultiTenanted();
                        m.Schema.For<NodeCounterB>().MultiTenanted();
                        m.Projections.Snapshot<NodeCounterA>(SnapshotLifecycle.Async);
                        m.Projections.Snapshot<NodeCounterB>(SnapshotLifecycle.Async);
                    })
                    .IntegrateWithWolverine(m => m.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                // Provision the (partitioned) schema eagerly and in order at startup.
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    [Fact]
    public async Task agents_spread_one_per_node_across_the_cluster_and_stay_per_shard()
    {
        await theOriginalHost.WaitUntilAssumesLeadershipAsync(10.Seconds());

        // Single node: both shards run here (2 agents, not 2 × 3 tenants).
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 2);
        }, 60.Seconds());

        // Add a second node — the two shards rebalance one-per-node.
        var second = await StartHostAsync();
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 1);
            w.ExpectRunningAgents(second, 1);
        }, 60.Seconds());

        // Exactly two agents cluster-wide, keyed by (store/database/shard) — no tenant component.
        var uris = await GetAgentUrisAsync(theOriginalHost);
        uris.Length.ShouldBe(2);
        uris.ShouldAllBe(u => !u.Contains("tenant1") && !u.Contains("tenant2"));
    }

    [Fact]
    public async Task agents_fail_over_to_the_surviving_node_when_a_node_leaves()
    {
        await theOriginalHost.WaitUntilAssumesLeadershipAsync(10.Seconds());

        var second = await StartHostAsync();
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 1);
            w.ExpectRunningAgents(second, 1);
        }, 60.Seconds());

        // The second node leaves the cluster — its subscription agent must reassign to the survivor,
        // and stay per-shard (still two agents total, both now on the original node).
        second.GetRuntime().Agents.DisableHealthChecks();
        await second.StopAsync();

        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 2);
        }, 60.Seconds());

        (await GetAgentUrisAsync(theOriginalHost)).Length.ShouldBe(2);
    }

    private static async Task<string[]> GetAgentUrisAsync(IHost host)
    {
        var family = host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();
        var agents = await family.AllKnownAgentsAsync();
        return [.. agents.Select(x => x.AbsoluteUri)];
    }
}

public record NodeTick(int Amount);

public class NodeCounterA
{
    public string Id { get; set; } = null!;
    public int Total { get; set; }
    public void Apply(NodeTick e) => Total += e.Amount;
}

public class NodeCounterB
{
    public string Id { get; set; } = null!;
    public int Total { get; set; }
    public void Apply(NodeTick e) => Total += e.Amount;
}
