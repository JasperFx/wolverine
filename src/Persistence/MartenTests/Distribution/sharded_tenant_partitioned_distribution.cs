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
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

// Phase 3 of the per-tenant-partitioned-events matrix — the #3280 regression itself: under
// MultiTenantedWithShardedDatabases + UseTenantPartitionedEvents, multiple tenants are CO-LOCATED in one
// shard database, each drawing its own event sequence. A single store-global agent per (shard, database)
// cannot track them (the lagging tenant's appends fall below the shared high-water mark and are skipped),
// so managed distribution must fan out one agent per (shard, tenant) — asserted here end-to-end: two
// ":all/<tenant>" agents for the co-located database, and both tenants' events actually projected.
//
// NOTE: this requires the companion JasperFx.Events (jasperfx#482) + Marten (marten#4799) changes — it
// goes green in CI once those merge, per the PR's stated merge order.
public class sharded_tenant_partitioned_distribution(ITestOutputHelper output) : PostgresqlContext, IAsyncLifetime
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    // Unique names per run: managed partitions/databases from one run otherwise break the next run's
    // resource-setup DDL reconciliation.
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private string ShardDatabase => $"w3281_shard_{theSuffix}";
    private string MasterSchema => $"csp_sharded_{theSuffix}";

    public async Task InitializeAsync()
    {
        // Provision the physical shard database (the master/pool lives in the default test database
        // under its own schema).
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            if (!await conn.DatabaseExists(ShardDatabase))
            {
                await new DatabaseSpecification().BuildDatabase(conn, ShardDatabase);
            }
        }

        var shardConnectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
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
                x.AddDatabase("shard-1", shardConnectionString);
            });

            m.Events.StreamIdentity = StreamIdentity.AsString;
            m.Events.TenancyStyle = TenancyStyle.Conjoined;
            m.Events.AppendMode = EventAppendMode.Quick;
            m.Events.UseTenantPartitionedEvents = true;
            m.Events.UseIdentityMapForAggregates = false;

            m.Schema.For<PartitionedCounter>().MultiTenanted();
            m.Projections.Snapshot<PartitionedCounter>(SnapshotLifecycle.Async);
        }

        // Provision the two CO-LOCATED tenants BEFORE the Wolverine host starts, so the first agent
        // enumeration already sees them and fans out per tenant. (Adding a database's FIRST tenants while
        // the host runs leaves the store-global agent from the empty-database phase running alongside the
        // new per-tenant agents until assignments reconcile — a transition edge outside this test's scope.)
        await using (var provisioning = DocumentStore.For(ConfigureShardedStore))
        {
            // Apply the event schema (parent partitioned tables) to the shard database first, so the
            // per-tenant LIST partitions + sequences created by the explicit assignment have a parent.
            await provisioning.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await provisioning.Advanced.AddTenantToShardAsync(TenantA, "shard-1", default);
            await provisioning.Advanced.AddTenantToShardAsync(TenantB, "shard-1", default);
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
    public async Task co_located_tenants_get_their_own_agents_and_both_project()
    {
        await theHost.WaitUntilAssumesLeadershipAsync(10.Seconds());

        // One async projection over one shard database with two co-located tenants => one agent PER TENANT.
        await theHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theHost, 2);
        }, 60.Seconds());

        var uris = await GetAgentUrisAsync(theHost);
        uris.Length.ShouldBe(2);
        uris.ShouldContain(u => u.TrimEnd('/').EndsWith("/" + TenantA, StringComparison.OrdinalIgnoreCase));
        uris.ShouldContain(u => u.TrimEnd('/').EndsWith("/" + TenantB, StringComparison.OrdinalIgnoreCase));

        // Both tenants' events are projected — each tenant advances against its OWN high-water mark, so the
        // second tenant's appends are not skipped below a shared mark (the #3280 failure mode).
        var id = "pc-" + Guid.NewGuid().ToString("N");
        await AppendAsync(TenantA, id, 5);
        await AppendAsync(TenantB, id, 11); // same stream id, other tenant partition

        (await WaitForProjectionAsync(TenantA, id, 60.Seconds()))!.Total.ShouldBe(5);
        (await WaitForProjectionAsync(TenantB, id, 60.Seconds()))!.Total.ShouldBe(11);
    }

    private async Task AppendAsync(string tenant, string id, int amount)
    {
        await using var session = theStore.LightweightSession(tenant);
        session.Events.StartStream<PartitionedCounter>(id, new CounterChanged(amount));
        await session.SaveChangesAsync();
    }

    private async Task<PartitionedCounter?> WaitForProjectionAsync(string tenant, string id, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var session = theStore.QuerySession(tenant);
            var doc = await session.LoadAsync<PartitionedCounter>(id);
            if (doc != null)
            {
                return doc;
            }

            await Task.Delay(250.Milliseconds());
        }

        return null;
    }

    private static async Task<string[]> GetAgentUrisAsync(IHost host)
    {
        var family = host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();
        var agents = await family.AllKnownAgentsAsync();
        return [.. agents.Select(x => x.AbsoluteUri)];
    }
}
