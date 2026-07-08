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

// JasperFx/jasperfx#486, the database-affine half of the revised #3280 design: with TWO shard databases
// (two co-located tenants each) across a TWO-node cluster, the per-(shard, tenant) agents must be assigned
// with database affinity — each shard database's agents land together on a single node — because the
// Marten store reports a multi-database cardinality. No opt-in configuration anywhere.
public class sharded_two_databases_affine_colocation(ITestOutputHelper output) : PostgresqlContext, IAsyncLifetime
{
    private readonly List<IHost> _hosts = [];

    // Unique names per run: managed partitions/databases from one run otherwise break the next run's
    // resource-setup DDL reconciliation.
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private string ShardDatabase1 => $"w3281_aff1_{theSuffix}";
    private string ShardDatabase2 => $"w3281_aff2_{theSuffix}";
    private string MasterSchema => $"csp_affine_{theSuffix}";

    private void ConfigureShardedStore(StoreOptions m)
    {
        string ConnectionStringFor(string database) => new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = database
        }.ConnectionString;

        m.DisableNpgsqlLogging = true;
        m.MultiTenantedWithShardedDatabases(x =>
        {
            x.ConnectionString = Servers.PostgresConnectionString;
            x.SchemaName = MasterSchema;
            x.PartitionSchemaName = MasterSchema + "_tenants";
            x.AddDatabase("shard-1", ConnectionStringFor(ShardDatabase1));
            x.AddDatabase("shard-2", ConnectionStringFor(ShardDatabase2));
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
            foreach (var database in new[] { ShardDatabase1, ShardDatabase2 })
            {
                if (!await conn.DatabaseExists(database))
                {
                    await new DatabaseSpecification().BuildDatabase(conn, database);
                }
            }
        }

        // Two co-located tenants PER SHARD DATABASE, provisioned before any host starts so the first
        // agent enumeration already fans out per tenant.
        await using (var provisioning = DocumentStore.For(ConfigureShardedStore))
        {
            await provisioning.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await provisioning.Advanced.AddTenantToShardAsync("alpha1", "shard-1", default);
            await provisioning.Advanced.AddTenantToShardAsync("alpha2", "shard-1", default);
            await provisioning.Advanced.AddTenantToShardAsync("beta1", "shard-2", default);
            await provisioning.Advanced.AddTenantToShardAsync("beta2", "shard-2", default);
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

    [Fact]
    public async Task each_shard_databases_agents_are_co_located_on_one_node()
    {
        var leader = await StartHostAsync();
        await leader.WaitUntilAssumesLeadershipAsync(10.Seconds());

        var second = await StartHostAsync();

        // 1 async projection × 2 tenants per database × 2 databases = 4 per-tenant agents; whole-database
        // groups of equal size across two nodes balance 2 + 2.
        await leader.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(leader, 2);
            w.ExpectRunningAgents(second, 2);
        }, 60.Seconds());

        // Every shard database's agents run whole on exactly one node.
        var byDatabase = _hosts
            .SelectMany(host => host.GetRuntime().Agents.AllRunningAgentUris()
                .Where(u => u.Scheme == EventSubscriptionAgentFamily.SchemeName)
                .Select(uri => (Node: host, Uri: uri)))
            .GroupBy(x => EventSubscriptionAgentFamily.DatabaseKeyOf(x.Uri))
            .ToList();

        byDatabase.Count.ShouldBe(2, "two shard databases, each one agent group");
        foreach (var databaseGroup in byDatabase)
        {
            databaseGroup.Select(x => x.Node).Distinct().Count().ShouldBe(1,
                $"agents of {databaseGroup.Key} must all run on the same node");
            databaseGroup.Count().ShouldBe(2, "one agent per co-located tenant");
        }

        // And the two databases are actually spread — one per node, not both piled on one.
        byDatabase.Select(g => g.Select(x => x.Node).Distinct().Single()).Distinct().Count().ShouldBe(2);

        // GH-3340: each node exposes the databases it owns via the public API, matching exactly the
        // databases of the agents actually running on it — so per-node work doesn't fan out to every shard.
        foreach (var host in _hosts)
        {
            var expected = host.GetRuntime().Agents.AllRunningAgentUris()
                .Where(u => u.Scheme == EventSubscriptionAgentFamily.SchemeName)
                .Select(EventSubscriptionAgentFamily.DatabaseIdOf)
                .Where(id => id != null)
                .Select(id => id!)
                .Distinct()
                .ToList();

            var owned = host.GetRuntime().Agents.AllLocallyOwnedDatabaseIds();

            owned.Count.ShouldBe(1, "each node owns exactly one of the two shard databases");
            owned.ShouldBe(expected);
        }

        // The two nodes own disjoint database sets that together cover both shard databases.
        _hosts.SelectMany(h => h.GetRuntime().Agents.AllLocallyOwnedDatabaseIds())
            .Distinct().Count().ShouldBe(2);
    }
}
