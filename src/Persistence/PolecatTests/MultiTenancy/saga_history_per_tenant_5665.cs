using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Polecat;

namespace PolecatTests.MultiTenancy;

// GH-5665: Polecat with a database per tenant. Each tenant's saga documents and its saga history land in that tenant's
// database, the history in the tenant's Wolverine message store, in the same transaction as the saga
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
        var catalog = Servers.SqlServerDatabaseName;
        var name = string.Equals(catalog, "master", StringComparison.OrdinalIgnoreCase)
            ? $"polecat_saga_history_{tenant}"
            : $"{catalog}_polecat_saga_history_{tenant}";

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT DB_ID(@name)";
            check.Parameters.AddWithValue("@name", name);
            var exists = await check.ExecuteScalarAsync();
            if (exists is null || exists == DBNull.Value)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{name}]";
                await create.ExecuteNonQueryAsync();
            }
        }

        return new SqlConnectionStringBuilder(Servers.SqlServerConnectionString) { InitialCatalog = name }
            .ConnectionString;
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddPolecat(m =>
            {
                m.ConnectionString = Servers.SqlServerConnectionString;
                m.DatabaseSchemaName = "saga_history_mt";
                m.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddTenant(Red, _red);
                    tenancy.AddTenant(Blue, _blue);
                });
            })
            .IntegrateWithWolverine(w =>
            {
                w.MessageStorageSchemaName = "saga_history_mt_wolverine";
                w.MainDatabaseConnectionString = Servers.SqlServerConnectionString;
            });

        opts.Services.AddResourceSetupOnStartup();
    }
}
