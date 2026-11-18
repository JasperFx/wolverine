using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.MultiTenancy;

// GH-5665: under database-per-tenant message storage, the lightweight saga tables and the saga history both live in
// each tenant's database
public class saga_history_per_tenant_5665 : SagaHistoryMultiTenancyCompliance
{
    private string _red = null!;
    private string _blue = null!;

    protected override async Task CreateTenantDatabasesAsync()
    {
        _red = await HistoryTenantDatabases.CreateAsync("red");
        _blue = await HistoryTenantDatabases.CreateAsync("blue");
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<HistorySaga>();
        opts.AddSagaType<RetainedHistorySaga>();

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_history_mt")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register(Red, _red);
                tenants.Register(Blue, _blue);
            });

        opts.Services.AddResourceSetupOnStartup();
    }
}

/// <summary>GH-5665. A PostgreSQL database of its own for each tenant of the saga history specs</summary>
public static class HistoryTenantDatabases
{
    public static async Task<string> CreateAsync(string tenant)
    {
        var name = Servers.PostgresDatabaseName == "postgres"
            ? $"saga_history_{tenant}"
            : $"{Servers.PostgresDatabaseName}_saga_history_{tenant}";

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        if (!await conn.DatabaseExists(name))
        {
            await new DatabaseSpecification().BuildDatabase(conn, name);
        }

        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Database = name }.ConnectionString;
    }
}
