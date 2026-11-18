using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Marten;

namespace MartenTests.MultiTenancy;

// GH-5665: Marten with a database per tenant. Each tenant's saga documents and its saga history land in that tenant's
// database, the history in the tenant's Wolverine message store, in the same session as the saga
public class saga_history_per_tenant_5665 : SagaHistoryMultiTenancyCompliance
{
    private string _red = null!;
    private string _blue = null!;

    protected override async Task CreateTenantDatabasesAsync()
    {
        _red = await createAsync("red");
        _blue = await createAsync("blue");
    }

    private static async Task<string> createAsync(string tenant)
    {
        var name = Servers.PostgresDatabaseName == "postgres"
            ? $"marten_saga_history_{tenant}"
            : $"{Servers.PostgresDatabaseName}_marten_saga_history_{tenant}";

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        if (!await conn.DatabaseExists(name))
        {
            await new DatabaseSpecification().BuildDatabase(conn, name);
        }

        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Database = name }.ConnectionString;
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
            {
                m.DatabaseSchemaName = "saga_history_mt";
                m.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddSingleTenantDatabase(_red, Red);
                    tenancy.AddSingleTenantDatabase(_blue, Blue);
                });
            })
            .IntegrateWithWolverine(w =>
            {
                w.MessageStorageSchemaName = "saga_history_mt_wolverine";
                w.MainDatabaseConnectionString = Servers.PostgresConnectionString;
            });

        opts.Services.AddResourceSetupOnStartup();
    }
}
