using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Tracking;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace MartenTests.MultiTenancy;

public class MultiTenancyContext : IClassFixture<MultiTenancyFixture>
{
    public MultiTenancyFixture Fixture { get; }

    public MultiTenancyContext(MultiTenancyFixture fixture)
    {
        Fixture = fixture;
        Runtime = fixture.Host.GetRuntime();
        Stores = Runtime.Storage.ShouldBeOfType<MultiTenantedMessageStore>();
    }

    public MultiTenantedMessageStore Stores { get; }

    public WolverineRuntime Runtime { get; }
}

public class MultiTenancyFixture : IAsyncLifetime
{
    public IHost? Host { get; private set; }

    private async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }

        builder.Database = databaseName;

        return builder.ConnectionString;
    }

    public async Task RestartAsync()
    {
        if (Host != null)
        {
            await Host.StopAsync();
            Host.Dispose();
        }

        await InitializeAsync();
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant1");
        var tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant2");
        var tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant3");


        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.DatabaseSchemaName = "mt";
                    m.MultiTenantedDatabases(tenancy =>
                    {
                        tenancy.AddSingleTenantDatabase(tenant1ConnectionString, "tenant1");
                        tenancy.AddSingleTenantDatabase(tenant2ConnectionString, "tenant2");
                        tenancy.AddSingleTenantDatabase(tenant3ConnectionString, "tenant3");
                    });

                })
                .IntegrateWithWolverine(m =>
                {
                    m.MessageStorageSchemaName = "control";
                    m.MasterDatabaseConnectionString = Servers.PostgresConnectionString;
                });

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await conn.CloseAsync();
    }

    public async Task DisposeAsync()
    {
        if (Host != null)
        {
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}