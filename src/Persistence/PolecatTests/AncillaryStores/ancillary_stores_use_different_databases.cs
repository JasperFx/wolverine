using IntegrationTests;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Polecat;
using Wolverine.Polecat.Publishing;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

// GH-3109 (Polecat mirror of MartenTests' ancillary_stores_use_different_databases): ancillary Polecat
// stores can live on separate SQL Server databases, and a multi-tenanted ancillary store (database per
// tenant) produces a MultiTenantedMessageStore. Mirrors the Marten coverage adapted to Polecat's
// separate-database multi-tenancy (Polecat has no UseTenantPartitionedEvents — see notes.md).
public class ancillary_stores_use_different_databases : IAsyncLifetime
{
    private IHost theHost = null!;
    private string playersConnectionString = null!;
    private string tenant1ConnectionString = null!;
    private string tenant2ConnectionString = null!;
    private IAgentFamily theStores = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            playersConnectionString = await CreateDatabaseIfNotExists(conn, "polecat_players_db");
            tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "polecat_things_t1");
            tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "polecat_things_t2");
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "diffdb_main";
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                // Single-database ancillary store on its own SQL Server database.
                opts.Services.AddPolecatStore<IPlayerStore>(m =>
                    {
                        m.Connection(playersConnectionString);
                        m.DatabaseSchemaName = "players";
                    })
                    .IntegrateWithWolverine();

                // Multi-tenanted ancillary store: database per tenant.
                opts.Services.AddPolecatStore<IThingStore>(m =>
                    {
                        // Polecat still needs a base connection string (the default-tenant / master
                        // database) even when separate per-tenant databases are configured.
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.MultiTenantedDatabases(tenancy =>
                        {
                            tenancy.AddTenant("tenant1", tenant1ConnectionString);
                            tenancy.AddTenant("tenant2", tenant2ConnectionString);
                        });
                        m.DatabaseSchemaName = "things";
                    })
                    .IntegrateWithWolverine(x => x.MainConnectionString = Servers.SqlServerConnectionString);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStores = theHost.GetRuntime().Stores;
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static async Task<string> CreateDatabaseIfNotExists(SqlConnection conn, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(Servers.SqlServerConnectionString);

        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT DB_ID(@name)";
            check.Parameters.AddWithValue("@name", databaseName);
            var exists = await check.ExecuteScalarAsync();
            if (exists is null || exists == DBNull.Value)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{databaseName}]";
                await create.ExecuteNonQueryAsync();
            }
        }

        builder.InitialCatalog = databaseName;
        return builder.ConnectionString;
    }

    [Fact]
    public void registers_the_single_tenant_ancillary_store_on_its_own_database()
    {
        var ancillaries = theHost.Services.GetServices<AncillaryMessageStore>().ToList();
        var player = ancillaries.Single(x => x.MarkerType == typeof(IPlayerStore));
        player.Inner.ShouldNotBeNull();
    }

    [Fact]
    public void registers_the_multi_tenanted_ancillary_store()
    {
        var ancillaries = theHost.Services.GetServices<AncillaryMessageStore>().ToList();
        ancillaries.Single(x => x.MarkerType == typeof(IThingStore))
            .Inner
            .ShouldBeOfType<MultiTenantedMessageStore>();
    }

    [Fact]
    public async Task open_session_for_player_store_targets_its_own_database()
    {
        var factory = theHost.Services.GetRequiredService<OutboxedSessionFactory<IPlayerStore>>();
        var context = new MessageContext(theHost.GetRuntime());

        await using var session = factory.OpenSession(context);

        // OpenSession overrides the context's storage to the ancillary store; its agent Uri must
        // reference the players database rather than the primary.
        context.Storage.Uri.ToString().ShouldContain("polecat_players_db");
    }

    [Fact]
    public async Task durability_agents_exist_for_every_ancillary_database()
    {
        var uris = (await theStores.AllKnownAgentsAsync()).Select(x => x.ToString()).ToArray();

        uris.ShouldContain(x => x.Contains("polecat_players_db"));
        uris.ShouldContain(x => x.Contains("polecat_things_t1"));
        uris.ShouldContain(x => x.Contains("polecat_things_t2"));
    }
}
