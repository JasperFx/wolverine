using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace PolecatTests.MultiTenancy;

// GH-3445: the PRIMARY Polecat store honors database-per-tenant. Before the fix, IntegrateWithWolverine()
// unconditionally built a single non-tenanted SqlServerMessageStore and never read
// MainDatabaseConnectionString, so tenant envelopes silently landed in the wrong database. Mirrors the
// ancillary coverage (ancillary_stores_use_different_databases) and the Marten primary MultiTenancyFixture.
public class primary_store_database_per_tenant : IAsyncLifetime
{
    private IHost theHost = null!;
    private string tenant1ConnectionString = null!;
    private string tenant2ConnectionString = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "polecat_primary_t1");
            tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "polecat_primary_t2");
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddPolecat(m =>
                    {
                        // Polecat still needs a base/default-tenant connection string even with
                        // separate per-tenant databases configured.
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "primary_mt";
                        m.MultiTenantedDatabases(tenancy =>
                        {
                            tenancy.AddTenant("tenant1", tenant1ConnectionString);
                            tenancy.AddTenant("tenant2", tenant2ConnectionString);
                        });
                    })
                    .UseLightweightSessions()
                    // MainDatabaseConnectionString is the tenant-neutral "master" store for nodes,
                    // assignments, and dead letters.
                    .IntegrateWithWolverine(x => x.MainDatabaseConnectionString = Servers.SqlServerConnectionString);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
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
    public void primary_store_is_multi_tenanted()
    {
        // Before GH-3445 this was a plain SqlServerMessageStore and MultiTenanted was empty.
        theHost.GetRuntime().Stores.MultiTenanted
            .ShouldContain(x => x is MultiTenantedMessageStore);
    }

    [Fact]
    public async Task durability_agents_exist_for_every_tenant_database()
    {
        var uris = (await theHost.GetRuntime().Stores.AllKnownAgentsAsync())
            .Select(x => x.ToString()).ToArray();

        uris.ShouldContain(x => x.Contains("polecat_primary_t1"));
        uris.ShouldContain(x => x.Contains("polecat_primary_t2"));
    }
}
