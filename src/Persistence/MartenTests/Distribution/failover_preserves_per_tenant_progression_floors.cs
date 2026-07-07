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
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

// JasperFx/jasperfx#486 WS1 — node failover must preserve each tenant's progression floor. Under
// MultiTenantedWithShardedDatabases + UseTenantPartitionedEvents two tenants are co-located in one shard
// database, each with its own agent and its own per-tenant progression row
// (`{Projection}:All:{tenant}` in mt_event_progression). The database-affine distribution keeps both
// agents together on ONE node of the two-node cluster; when that node leaves, the surviving node must
// (1) pick both agents up, (2) resume each tenant EXACTLY where its progression row left off — events
// appended after the failover project on top of the pre-failover state with nothing skipped — and
// (3) never rewind a progression row below its pre-failover floor (a rewind would mean reprocessing,
// which the additive Apply() below would surface as an inflated Total).
//
// NOTE: the group of agents may land whole on either node (whichever way the leader balances the
// affine group against its own baseline agents), and it can be rebalanced a beat after the second node
// joins — the test resolves the owner dynamically and only after the placement holds stable across
// several assignment-evaluation periods.
//
// ── SKIPPED: pinned Marten product bug (frozen per-tenant high-water) ─────────────────────────────────
// The failover mechanics themselves work (verified from the run logs + diagnostics of this test before
// it was skipped): the survivor picks up both per-tenant agents, tenant-a's doc and both progression
// floors come through intact, and nothing ever rewinds. What fails is the LAST leg — the M post-failover
// tenant-b events never project, because per-tenant high-water detection FREEZES at the first persisted
// per-tenant high-water row in published Marten 9.13.0-alpha.2:
//
//   Marten src/Marten/Events/Daemon/HighWater/HighWaterDetector.cs, loadPerTenantStatistics (:260):
//       var currentMark = lastSeqId > 0 ? lastSeqId : lastValue;
//   where lastSeqId = the persisted `HighWaterMark:<tenant>` mt_event_progression row and
//   lastValue = max(seq_id) of the tenant's events (per #4847). The FIRST poll after a tenant's first
//   events works (no row yet → lastSeqId = 0 → CurrentMark = max(seq_id)) and then persists the row via
//   TenantedHighWaterCoordinator.PollAndRouteAsync → MarkHighWaterForTenantAsync. Every subsequent poll
//   reads the row back as CurrentMark and re-persists the same value — max(seq_id) is never consulted
//   again (the "per-tenant gap detection not yet wired in (a Phase 3 refinement)" comment right above
//   that line is the gap). So ANY second batch of events for a tenant stalls forever — not just across
//   failover; the sibling tests never see it because they all append exactly one batch per tenant.
//
// Empirically pinned here (diagnostics from the failing run): tenant-b had 11 committed events
// (max seq_id = 11 after a probe append), the store-global HighWaterMark row advanced to 11 (global
// detection + the JasperFx per-tenant poll trigger both fired — JasperFx.Events
// JasperFxAsyncDaemon.OnNext reuses the global high-water cadence), yet HighWaterMark:tenant-b and
// PartitionedCounter:All:tenant-b stayed frozen at 6 = the first persisted mark.
//
// Unskip (and delete this block) once Marten advances per-tenant CurrentMark past the persisted row
// (e.g. max(lastSeqId, lastValue) or real per-tenant gap detection).
public class failover_preserves_per_tenant_progression_floors(ITestOutputHelper output)
    : PostgresqlContext, IAsyncLifetime
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private readonly List<IHost> _hosts = [];
    private string theShardConnectionString = null!;

    // Unique names per run: managed partitions/databases from one run otherwise break the next run's
    // resource-setup DDL reconciliation.
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private string ShardDatabase => $"w486_fo_{theSuffix}";
    private string MasterSchema => $"csp_fo_{theSuffix}";

    private void ConfigureShardedStore(StoreOptions m)
    {
        m.DisableNpgsqlLogging = true;
        m.MultiTenantedWithShardedDatabases(x =>
        {
            x.ConnectionString = Servers.PostgresConnectionString;
            x.SchemaName = MasterSchema;
            x.PartitionSchemaName = MasterSchema + "_tenants";
            x.AddDatabase("shard-1", theShardConnectionString);
        });

        m.Events.StreamIdentity = StreamIdentity.AsString;
        m.Events.TenancyStyle = TenancyStyle.Conjoined;
        m.Events.AppendMode = EventAppendMode.Quick;
        m.Events.UseTenantPartitionedEvents = true;
        m.Events.UseIdentityMapForAggregates = false;

        m.Schema.For<PartitionedCounter>().MultiTenanted();
        m.Projections.Snapshot<PartitionedCounter>(SnapshotLifecycle.Async);
    }

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            if (!await conn.DatabaseExists(ShardDatabase))
            {
                await new DatabaseSpecification().BuildDatabase(conn, ShardDatabase);
            }
        }

        theShardConnectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = ShardDatabase
        }.ConnectionString;

        // Two co-located tenants on the one shard database, provisioned before any host starts so the
        // first agent enumeration already fans out per tenant.
        await using (var provisioning = DocumentStore.For(ConfigureShardedStore))
        {
            await provisioning.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await provisioning.Advanced.AddTenantToShardAsync(TenantA, "shard-1", default);
            await provisioning.Advanced.AddTenantToShardAsync(TenantB, "shard-1", default);
        }
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

                opts.Services.AddMarten(ConfigureShardedStore)
                    .IntegrateWithWolverine(m =>
                    {
                        m.UseWolverineManagedEventSubscriptionDistribution = true;
                        // Sharded tenancy has no single "main" database, so Wolverine's message store needs
                        // an explicit master.
                        m.MainDatabaseConnectionString = Servers.PostgresConnectionString;
                    });

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    [Fact(Skip = "Pinned Marten 9.13.0-alpha.2 bug: per-tenant high-water freezes at the first persisted " +
                 "HighWaterMark:<tenant> row (HighWaterDetector.loadPerTenantStatistics, " +
                 "src/Marten/Events/Daemon/HighWater/HighWaterDetector.cs:260 — " +
                 "`lastSeqId > 0 ? lastSeqId : lastValue`), so any second batch of events per tenant " +
                 "never projects. See the class comment for the full mechanism; unskip on the Marten fix.")]
    public async Task surviving_node_resumes_each_tenant_from_its_progression_floor()
    {
        const int n = 6; // events per tenant before the failover
        const int m = 4; // tenant-b events appended after the node dies

        var first = await StartHostAsync();
        await first.WaitUntilAssumesLeadershipAsync(10.Seconds());

        var second = await StartHostAsync();

        // One shard database × two tenants = one affine group of two agents, whole on exactly one node.
        var owner = await WaitForSingleOwnerAsync(60.Seconds());
        var survivor = ReferenceEquals(owner, first) ? second : first;

        // Pre-failover: N events per tenant, both projected.
        var store = owner.Services.GetRequiredService<IDocumentStore>();
        var idA = "pc-" + Guid.NewGuid().ToString("N");
        var idB = "pc-" + Guid.NewGuid().ToString("N");
        await AppendAsync(store, TenantA, idA, n);
        await AppendAsync(store, TenantB, idB, n);

        var survivorStore = survivor.Services.GetRequiredService<IDocumentStore>();
        (await WaitForTotalAsync(survivorStore, TenantA, idA, n, 60.Seconds()))!.Total.ShouldBe(n);
        (await WaitForTotalAsync(survivorStore, TenantB, idB, n, 60.Seconds()))!.Total.ShouldBe(n);

        // Record each tenant's progression floor (its own per-tenant sequence, so exactly N here).
        var floorA = (await TenantProgressionAsync($"PartitionedCounter:All:{TenantA}")).ShouldNotBeNull();
        var floorB = (await TenantProgressionAsync($"PartitionedCounter:All:{TenantB}")).ShouldNotBeNull();
        floorA.ShouldBeGreaterThanOrEqualTo(n);
        floorB.ShouldBeGreaterThanOrEqualTo(n);

        // Kill the node that owns tenant-b's agent (both agents, by affinity). Same stop pattern as
        // tenant_partitioned_distribution_multinode.agents_fail_over_to_the_surviving_node_when_a_node_leaves.
        owner.GetRuntime().Agents.DisableHealthChecks();
        await owner.StopAsync();

        // If the owner was the leader (the usual case — see class comment), the survivor must first
        // assume leadership before it can evaluate assignments. Returns immediately when the survivor
        // already leads.
        await survivor.WaitUntilAssumesLeadershipAsync(60.Seconds());

        await survivor.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(survivor, 2);
        }, 60.Seconds());

        // Post-failover: M more events for tenant-b, appended through the surviving node's store.
        await AppendAsync(survivorStore, TenantB, idB, m);

        // The projected doc must reflect EXACTLY N+M: a skipped event (resuming above the floor) would
        // leave Total below, a reprocessed one (progression rewound below the floor) would push it above
        // because Apply() is additive. While polling, also assert the progression row NEVER dips below
        // the pre-failover floor.
        var deadline = DateTimeOffset.UtcNow + 60.Seconds();
        PartitionedCounter? docB = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var progression = await TenantProgressionAsync($"PartitionedCounter:All:{TenantB}");
            if (progression.HasValue)
            {
                progression.Value.ShouldBeGreaterThanOrEqualTo(floorB,
                    "tenant-b's progression must never rewind below its pre-failover floor");
            }

            docB = await LoadAsync(survivorStore, TenantB, idB);
            if (docB?.Total >= n + m)
            {
                break;
            }

            await Task.Delay(250.Milliseconds());
        }

        docB.ShouldNotBeNull();
        docB.Total.ShouldBe(n + m);

        // Progression advanced monotonically past the floor by the M new events.
        var finalB = (await TenantProgressionAsync($"PartitionedCounter:All:{TenantB}")).ShouldNotBeNull();
        finalB.ShouldBeGreaterThanOrEqualTo(floorB + m);

        // And the co-located tenant-a came through the failover untouched: same doc, no reprocessing,
        // floor intact.
        (await LoadAsync(survivorStore, TenantA, idA))!.Total.ShouldBe(n);
        var finalA = (await TenantProgressionAsync($"PartitionedCounter:All:{TenantA}")).ShouldNotBeNull();
        finalA.ShouldBeGreaterThanOrEqualTo(floorA);
    }

    /// <summary>
    /// Wait until exactly two event-subscription agents run cluster-wide AND both run on the same node
    /// (the database-affine grouping), returning that owning host. The owner must hold STABLY across
    /// several consecutive samples spanning multiple assignment evaluations (CheckAssignmentPeriod = 1s):
    /// right after the second node joins, the leader may still be running both agents from its
    /// single-node phase and rebalance the whole group a beat later — sampling once would race that move
    /// and hand back the wrong owner.
    /// </summary>
    private async Task<IHost> WaitForSingleOwnerAsync(TimeSpan timeout)
    {
        const int requiredStableSamples = 6; // 6 × 500ms = 3s > 2 assignment evaluation periods
        var deadline = DateTimeOffset.UtcNow + timeout;
        IHost? candidate = null;
        var stableSamples = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var byHost = _hosts
                .Select(host => (Host: host, Agents: host.GetRuntime().Agents.AllRunningAgentUris()
                    .Where(x => x.Scheme == EventSubscriptionAgentFamily.SchemeName)
                    .ToArray()))
                .Where(x => x.Agents.Length > 0)
                .ToArray();

            if (byHost.Length == 1 && byHost[0].Agents.Length == 2)
            {
                if (ReferenceEquals(byHost[0].Host, candidate))
                {
                    if (++stableSamples >= requiredStableSamples)
                    {
                        return candidate!;
                    }
                }
                else
                {
                    candidate = byHost[0].Host;
                    stableSamples = 1;
                }
            }
            else
            {
                candidate = null;
                stableSamples = 0;
            }

            await Task.Delay(500.Milliseconds());
        }

        throw new TimeoutException(
            "The two per-tenant agents never settled together on a single node. Running agents: " +
            _hosts.Select(h => h.GetRuntime().Agents.AllRunningAgentUris()
                    .Where(x => x.Scheme == EventSubscriptionAgentFamily.SchemeName)
                    .Select(x => x.ToString())
                    .Join(", "))
                .Join(" | "));
    }

    private static async Task AppendAsync(IDocumentStore store, string tenant, string id, int events)
    {
        await using var session = store.LightweightSession(tenant);
        var payload = Enumerable.Range(0, events).Select(_ => (object)new CounterChanged(1)).ToArray();
        if (await session.Events.FetchStreamStateAsync(id) == null)
        {
            session.Events.StartStream<PartitionedCounter>(id, payload);
        }
        else
        {
            session.Events.Append(id, payload);
        }

        await session.SaveChangesAsync();
    }

    private static async Task<PartitionedCounter?> LoadAsync(IDocumentStore store, string tenant, string id)
    {
        await using var session = store.QuerySession(tenant);
        return await session.LoadAsync<PartitionedCounter>(id);
    }

    private static async Task<PartitionedCounter?> WaitForTotalAsync(IDocumentStore store, string tenant,
        string id, int expectedTotal, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        PartitionedCounter? doc = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            doc = await LoadAsync(store, tenant, id);
            if (doc?.Total >= expectedTotal)
            {
                return doc;
            }

            await Task.Delay(250.Milliseconds());
        }

        return doc;
    }

    // Events (and therefore mt_event_progression) live in the store's default schema ("public" — the
    // sharded configuration's SchemaName only scopes the pool/assignment tables in the MASTER database)
    // of the shard database.
    private async Task<long?> TenantProgressionAsync(string shardIdentity)
    {
        await using var conn = new NpgsqlConnection(theShardConnectionString);
        await conn.OpenAsync();
        var result = await conn
            .CreateCommand("select last_seq_id from public.mt_event_progression where name = :name")
            .With("name", shardIdentity)
            .ExecuteScalarAsync();

        return result is long value ? value : null;
    }
}
