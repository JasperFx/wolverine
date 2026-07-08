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

// JasperFx/jasperfx#486 WS1 — runtime tenant churn under Wolverine-managed distribution. The sibling
// sharded_tenant_partitioned_distribution test provisions its co-located tenants BEFORE the host starts,
// so the first agent enumeration already fans out per tenant. This test covers the RUNTIME transition:
// with the host up and tenant-a's per-tenant agent already projecting, a NEW tenant is added to the same
// shard database via Advanced.AddTenantToShardAsync. Within the assignment-evaluation window
// (Durability.CheckAssignmentPeriod) the leader must re-enumerate the store's usage, see the new tenant,
// fan out its ":all/tenant-b" agent, and start projecting the new tenant's events from its OWN per-tenant
// sequence 1.. — all WITHOUT ever running a store-global (tenant-less) agent for the shard alongside the
// per-tenant agents, which would double-process the same events (the stale-agent-retirement edge of
// wolverine#3328, EventSubscriptionAgentFamily.RetireSupersededAgentsAsync).
//
// TENANT REMOVAL (the inverse transition) IS DELIBERATELY NOT COVERED END-TO-END, because no runtime
// removal path exists end-to-end in published Marten 9.13.0-alpha.2:
//   - Marten DOES expose removal APIs on ShardedTenancy (src/Marten/Storage/ShardedTenancy.cs):
//     RemoveTenantAsync(tenantId, ct) (:378) hard-deletes the pool assignment row, and
//     DisableTenantAsync(tenantId) (:470) soft-deletes (disabled = true) — both via the store-agnostic
//     IDynamicTenantSource<string> surface from marten#4607.
//   - BUT the running store's usage never shrinks: MartenDatabase.TenantIds is only ever ADDED to —
//     BuildDatabases() re-reads the assignment table on every call but only Fill()s ids in
//     (ShardedTenancy.cs:211), as do AssignTenantAsync (:364) and AddTenantToShardAsync (:660) — and
//     nothing ever removes an id from that in-memory list. DescribeDatabasesAsync (:735-751) copies
//     exactly that list onto the DatabaseDescriptor that IEventStore.TryCreateUsage returns.
//   - Wolverine's retirement only stops an agent that the current SupportedAgentsAsync enumeration no
//     longer lists (EventSubscriptionAgentFamily.RetireSupersededAgentsAsync), and EventStoreAgents
//     .SupportedAgentsAsync fans out from DatabaseDescriptor.TenantIds — which still contains the
//     removed tenant for the life of the store instance. So the removed tenant's agent keeps running
//     until a process restart (a FRESH store's BuildDatabases() no longer reads the deleted/disabled
//     row, so only then does the enumeration shrink and retirement fire).
//   - The Wolverine half of the removal transition IS pinned by the unit test
//     event_subscription_family_stale_agent_retirement.running_per_tenant_agent_is_stopped_when_its_tenant_is_removed
//     (CoreTests/Runtime/Agents/event_subscription_family_cardinality_assignment.cs), which stubs the
//     shrunken usage directly. The missing piece is Marten-side (TenantIds shrink on remove/disable),
//     not Wolverine-side.
public class runtime_tenant_churn_under_managed_distribution(ITestOutputHelper output)
    : PostgresqlContext, IAsyncLifetime
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private string theShardConnectionString = null!;

    // Unique names per run: managed partitions/databases from one run otherwise break the next run's
    // resource-setup DDL reconciliation.
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private string ShardDatabase => $"w486_churn_{theSuffix}";
    private string MasterSchema => $"csp_churn_{theSuffix}";

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

        void ConfigureShardedStore(StoreOptions m)
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

        // ONLY tenant-a is provisioned before the host starts. tenant-b arrives at runtime, in the test
        // body — that transition is the point of this test. Because the shard database already has a
        // tenant, the first enumeration fans out per tenant from the start and no store-global agent
        // ever legitimately exists for this shard.
        await using (var provisioning = DocumentStore.For(ConfigureShardedStore))
        {
            await provisioning.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await provisioning.Advanced.AddTenantToShardAsync(TenantA, "shard-1", default);
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                opts.Services.AddMarten(ConfigureShardedStore)
                    .IntegrateWithWolverine(m =>
                    {
                        m.UseWolverineManagedEventSubscriptionDistribution = true;
                        // Sharded tenancy has no single "main" database, so Wolverine's message store needs
                        // an explicit master (mirrors a real deployment pointing this at a references DB).
                        m.MainDatabaseConnectionString = Servers.PostgresConnectionString;
                    });

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        theHost.GetRuntime().Agents.DisableHealthChecks();
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task adding_a_tenant_at_runtime_fans_out_its_agent_within_the_assignment_window()
    {
        await theHost.WaitUntilAssumesLeadershipAsync(10.Seconds());

        // Phase 1: steady state with the pre-provisioned tenant only — exactly ONE per-tenant agent.
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theHost, 1);
        }, 60.Seconds());

        var running = RunningSubscriptionUris();
        running.Length.ShouldBe(1);
        running[0].ShouldEndWith("/" + TenantA);

        var idA = "pc-" + Guid.NewGuid().ToString("N");
        await AppendAsync(TenantA, idA, 5);
        (await WaitForProjectionAsync(TenantA, idA, 5, 60.Seconds()))!.Total.ShouldBe(5);

        // Phase 2: WHILE the host is running, add tenant-b to the same shard database through the
        // running store — the same provisioning path the sibling tests use before startup.
        await theStore.Advanced.AddTenantToShardAsync(TenantB, "shard-1", default);

        // Within the assignment-evaluation window (CheckAssignmentPeriod = 1s; generous ceiling for CI)
        // the leader re-enumerates the usage and starts tenant-b's agent.
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theHost, 2);
        }, 60.Seconds());

        running = RunningSubscriptionUris();
        running.Length.ShouldBe(2);
        running.ShouldContain(u => u.EndsWith("/" + TenantA, StringComparison.OrdinalIgnoreCase));
        running.ShouldContain(u => u.EndsWith("/" + TenantB, StringComparison.OrdinalIgnoreCase));

        // The wolverine#3328 transition edge: no store-global (tenant-less) agent may run alongside the
        // per-tenant agents — a lingering "…/partitionedcounter/all" agent would track the same events
        // as the per-tenant agents and double-process. A tenant-less agent URI ends with the bare "/all"
        // shard-key segment; every per-tenant URI carries a trailing tenant segment.
        running.ShouldAllBe(u => !u.TrimEnd('/').EndsWith("/all", StringComparison.OrdinalIgnoreCase));

        // Phase 3: tenant-b's fresh events — its OWN per-tenant sequence, from 1 — actually project…
        var idB = "pc-" + Guid.NewGuid().ToString("N");
        await AppendAsync(TenantB, idB, 7);
        (await WaitForProjectionAsync(TenantB, idB, 7, 60.Seconds()))!.Total.ShouldBe(7);

        // …and the per-tenant progression row exists in the shard database under the tenant-scoped shard
        // identity (`{Projection}:{ShardKey}:{tenant}` — see JasperFx ShardName.Identity and the comment
        // in Marten's EventProgressionTable). The progression write commits atomically with the projected
        // document above, so no extra polling is needed here.
        var progressionB = await TenantProgressionAsync($"PartitionedCounter:All:{TenantB}");
        progressionB.ShouldNotBeNull();
        progressionB.Value.ShouldBeGreaterThanOrEqualTo(7);

        // And tenant-a was undisturbed by the churn.
        (await LoadAsync(TenantA, idA))!.Total.ShouldBe(5);

        // Still no store-global agent after processing settled.
        RunningSubscriptionUris()
            .ShouldAllBe(u => !u.TrimEnd('/').EndsWith("/all", StringComparison.OrdinalIgnoreCase));
    }

    private string[] RunningSubscriptionUris()
    {
        return theHost.GetRuntime().Agents.AllRunningAgentUris()
            .Where(x => x.Scheme == EventSubscriptionAgentFamily.SchemeName)
            .Select(x => x.AbsoluteUri.TrimEnd('/'))
            .ToArray();
    }

    private async Task AppendAsync(string tenant, string id, int events)
    {
        await using var session = theStore.LightweightSession(tenant);
        session.Events.StartStream<PartitionedCounter>(id,
            Enumerable.Range(0, events).Select(_ => (object)new CounterChanged(1)).ToArray());
        await session.SaveChangesAsync();
    }

    private async Task<PartitionedCounter?> LoadAsync(string tenant, string id)
    {
        await using var session = theStore.QuerySession(tenant);
        return await session.LoadAsync<PartitionedCounter>(id);
    }

    private async Task<PartitionedCounter?> WaitForProjectionAsync(string tenant, string id, int expectedTotal,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        PartitionedCounter? doc = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            doc = await LoadAsync(tenant, id);
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
