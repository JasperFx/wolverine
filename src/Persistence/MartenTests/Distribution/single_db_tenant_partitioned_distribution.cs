using IntegrationTests;
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
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

// Regression test for marten#4862: SINGLE-database × Wolverine-managed cell of the distribution
// matrix (JasperFx/jasperfx#486 WS1).
//
// The bug was found by the WS1 fact-finding probe this test grew out of
// (single_db_tenant_partitioned_lagging_tenant_probe, 2026-07-06): under UseTenantPartitionedEvents
// every tenant draws seq_ids from its own mt_events_sequence_{tenant} starting at 1, so sequences
// OVERLAP inside the one database. Tenant A appends many events first (a store-global agent's
// progression floor advances to A's max), then tenant B appends its first few events at per-tenant
// seq 1..N — far BELOW that floor. Against Marten 9.13.0-alpha.2, where
// IEventStore.DistributesAgentsPerTenant required ShardedTenancy, Wolverine started only the single
// store-global "PartitionedCounter:All" agent, and the lagging tenant's events fell below the shared
// floor and were skipped forever — the wolverine#3280 failure mode, on a single database.
//
// Marten 9.13.0-alpha.3 fixes this (marten#4864: DistributesAgentsPerTenant broadened to ANY
// tenant-partitioned store, and single-DB store descriptors carry their TenantIds), so managed
// distribution now fans out one agent per tenant on a single database, each with its own per-tenant
// progression row — the lag shape is representable and nothing is skipped. This test pins BOTH the
// per-tenant fan-out (2 tenants -> 2 agents) and the lagging-tenant projection. (One residual
// upstream caveat: the per-tenant high-water POLL still rides the global high-water cadence —
// jasperfx#492 — so Phase 2 nudges the global mark to trigger it; see the comment there.)
public class single_db_tenant_partitioned_distribution(ITestOutputHelper output)
    : PostgresqlContext, IAsyncLifetime
{
    // Letters/digits/underscores only — the tenant id doubles as the partition suffix.
    private const string TenantA = "tenant1";
    private const string TenantB = "tenant2";

    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    // Unique schema per run: managed partitions created in one run otherwise break the next run's
    // resource-setup DDL reconciliation against the same schema.
    private readonly string theSchema = "csp_lag_" + Guid.NewGuid().ToString("N")[..8];

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

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
            [TenantA] = TenantA,
            [TenantB] = TenantB
        });
    }

    public async Task DisposeAsync()
    {
        theHost.GetRuntime().Agents.DisableHealthChecks();
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task lagging_tenant_events_are_projected_under_managed_distribution()
    {
        await theHost.WaitUntilAssumesLeadershipAsync(10.Seconds());

        // marten#4864 (Marten 9.13.0-alpha.3): a single-database tenant-partitioned store reports
        // DistributesAgentsPerTenant = true, so managed distribution fans out one agent PER TENANT —
        // two tenants => two agents, not one store-global agent.
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theHost, 2);
        }, 60.Seconds());

        output.WriteLine("Agent URIs after startup:");
        foreach (var uri in await GetAgentUrisAsync(theHost))
        {
            output.WriteLine("  " + uri);
        }

        // Phase 1 — the LEADING tenant: 20 events over several commits, per-tenant seq 1..20.
        var id = "pc-" + Guid.NewGuid().ToString("N");
        await StartStreamAsync(TenantA, id);
        for (var i = 0; i < 4; i++)
        {
            await AppendAsync(TenantA, id, 5);
        }

        // Wait until the agent has demonstrably processed ALL of tenant A's events — the store-global
        // progression floor is now at/above A's max seq (20).
        var docA = await WaitForProjectionAsync(TenantA, id, x => x.Total >= 21, 60.Seconds());
        docA.ShouldNotBeNull("The leading tenant's projection never caught up — the store-global agent is not processing at all");
        docA.Total.ShouldBe(21);

        await DumpProgressionAsync("after tenant A caught up");

        // Phase 2 — the LAGGING tenant appends its FIRST events now, at per-tenant seq 1..3: far below
        // the store-global floor of ~21.
        await StartStreamAsync(TenantB, id);
        await AppendAsync(TenantB, id, 2);

        // KNOWN CADENCE GAP (jasperfx#492 — same mitigation family as Marten's own
        // dynamic_tenant_lifecycle_during_continuous_daemon test): the vectorized per-tenant
        // high-water poll rides the GLOBAL high-water cadence, and tenant2's events at per-tenant
        // seq 1..3 do not move the store-global max(seq_id) (still 21 from tenant1), so nothing
        // triggers the poll that would advance HighWaterMark:tenant2 — the agent idles at ceiling 0.
        // Mitigation: NUDGE the global mark forward with tenant1 events while waiting; each nudge
        // (global seq 22, 23, ...) fires the per-tenant poll, which reads tenant2's OWN max(seq_id)
        // and routes its lagging events. The lag shape this test pins is untouched: under the OLD
        // single store-global agent (pre-marten#4864) the nudges cannot help — the store-global floor
        // only climbs FURTHER above tenant2's seq 1..3 — so the regression discrimination stands.
        // Drop the nudging once jasperfx#492 puts per-tenant polls on the timer instead.
        PartitionedCounter? docB = null;
        var deadline = DateTimeOffset.UtcNow + 45.Seconds();
        while (DateTimeOffset.UtcNow < deadline)
        {
            await AppendAsync(TenantA, id, 1); // move the global mark -> trigger the per-tenant poll
            docB = await WaitForProjectionAsync(TenantB, id, x => x.Total >= 3, 3.Seconds());
            if (docB?.Total >= 3)
            {
                break;
            }
        }

        await DumpProgressionAsync("after waiting for tenant B");
        output.WriteLine($"lagging tenant '{TenantB}' projection doc: {(docB == null ? "MISSING" : $"Total={docB.Total}")}");

        docB.ShouldNotBeNull(
            "wolverine#3280 on a single database: the lagging tenant's events (per-tenant seq 1..3) fell " +
            "below the store-global progression floor and were skipped by the single store-global agent");
        docB.Total.ShouldBe(3);
    }

    private async Task StartStreamAsync(string tenant, string id)
    {
        await using var session = theStore.LightweightSession(tenant);
        session.Events.StartStream<PartitionedCounter>(id, new CounterChanged(1));
        await session.SaveChangesAsync();
    }

    private async Task AppendAsync(string tenant, string id, int count)
    {
        await using var session = theStore.LightweightSession(tenant);
        for (var i = 0; i < count; i++)
        {
            session.Events.Append(id, new CounterChanged(1));
        }

        await session.SaveChangesAsync();
    }

    private async Task<PartitionedCounter?> WaitForProjectionAsync(
        string tenant, string id, Func<PartitionedCounter, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        PartitionedCounter? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var session = theStore.QuerySession(tenant);
            last = await session.LoadAsync<PartitionedCounter>(id);
            if (last != null && predicate(last))
            {
                return last;
            }

            await Task.Delay(250.Milliseconds());
        }

        return last;
    }

    private async Task DumpProgressionAsync(string label)
    {
        output.WriteLine($"mt_event_progression ({label}):");
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var reader = await conn
            .CreateCommand($"select name, last_seq_id from {theSchema}.mt_event_progression order by name")
            .ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.WriteLine($"  {reader.GetString(0)} => {reader.GetInt64(1)}");
        }
    }

    private static async Task<string[]> GetAgentUrisAsync(IHost host)
    {
        var family = host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();
        var agents = await family.AllKnownAgentsAsync();
        return [.. agents.Select(x => x.AbsoluteUri)];
    }
}
