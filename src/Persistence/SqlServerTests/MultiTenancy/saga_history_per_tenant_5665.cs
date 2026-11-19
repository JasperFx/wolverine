using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RDBMS;
using Wolverine.SqlServer;

namespace SqlServerTests.MultiTenancy;

// GH-5665: under database-per-tenant message storage, the lightweight saga tables and the saga history both live in
// each tenant's database
public class saga_history_per_tenant_5665 : SagaHistoryMultiTenancyCompliance
{
    private string _red = null!;
    private string _blue = null!;

    protected override async Task CreateTenantDatabasesAsync()
    {
        _red = await createAsync(LaneDatabases.Name("saga_history_red"));
        _blue = await createAsync(LaneDatabases.Name("saga_history_blue"));
    }

    private static async Task<string> createAsync(string databaseName)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand("SELECT DB_ID(@databaseName)"))
        {
            cmd.Parameters.AddWithValue("@databaseName", databaseName);
            var result = await cmd.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
            {
                await conn.CreateCommand($"CREATE DATABASE [{databaseName}]").ExecuteNonQueryAsync();
            }
        }

        return new SqlConnectionStringBuilder(Servers.SqlServerConnectionString) { InitialCatalog = databaseName }
            .ConnectionString;
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<HistorySaga>();
        opts.AddSagaType<RetainedHistorySaga>();

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_history_mt")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register(Red, _red);
                tenants.Register(Blue, _blue);
            });

        opts.Services.AddResourceSetupOnStartup();
    }
}
