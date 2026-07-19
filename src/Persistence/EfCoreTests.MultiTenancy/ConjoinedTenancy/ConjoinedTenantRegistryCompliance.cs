using IntegrationTests;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
///     Phase 3 battery (GH-3464): the wolverine_tenants registry as the
///     authoritative tenant list for conjoined tenancy, exposed through
///     IDynamicTenantSource for CritterWatch tenant management
/// </summary>
[Collection("multi-tenancy")]
public abstract class ConjoinedTenantRegistryCompliance : IAsyncLifetime
{
    private readonly DatabaseEngine _engine;
    protected IHost theHost = null!;
    protected IDynamicTenantSource<string> theSource = null!;

    protected ConjoinedTenantRegistryCompliance(DatabaseEngine engine)
    {
        _engine = engine;
    }

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<ConjoinedItemHandler>();

                if (_engine == DatabaseEngine.PostgreSQL)
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_registry");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedItemsDbContext>(
                        (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                        AutoCreate.CreateOrUpdate);
                }
                else
                {
                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "conjoined_registry");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedItemsDbContext>(
                        (builder, connectionString) => builder.UseSqlServer(connectionString.Value),
                        AutoCreate.CreateOrUpdate);
                }

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();
                opts.PublishAllMessages().Locally();
            }).StartAsync();

        theSource = theHost.Services.GetRequiredService<IDynamicTenantSource<string>>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<int> countRegistryRowsAsync(string tenantId)
    {
        var sql =
            $"SELECT COUNT(*) FROM conjoined_registry.wolverine_tenants WHERE tenant_id = '{tenantId}'";
        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        await using var sqlConn = new SqlConnection(Servers.SqlServerConnectionString);
        await sqlConn.OpenAsync();
        await using var sqlCmd = sqlConn.CreateCommand();
        sqlCmd.CommandText = sql;
        return Convert.ToInt32(await sqlCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task registry_add_disable_enable_remove_round_trip()
    {
        var tenant = "registry_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        await theSource.AddTenantAsync(tenant, CancellationToken.None);
        (await countRegistryRowsAsync(tenant)).ShouldBe(1);

        // A registered, enabled tenant can write
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync(tenant, new CreateConjoinedItem(id, "ok")));

        // Disabling stops writes with UnknownTenantIdException
        await theSource.DisableTenantAsync(tenant);
        (await theSource.AllDisabledAsync()).ShouldContain(tenant);
        await Should.ThrowAsync<UnknownTenantIdException>(() => theHost.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.InvokeForTenantAsync(tenant, new CreateConjoinedItem(Guid.NewGuid(), "no"))));

        // Re-enabling restores writes
        await theSource.EnableTenantAsync(tenant);
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync(tenant, new CreateConjoinedItem(Guid.NewGuid(), "again")));

        await theSource.RemoveTenantAsync(tenant);
        (await countRegistryRowsAsync(tenant)).ShouldBe(0);
    }

    [Fact]
    public async Task explicit_connection_string_registration_is_rejected()
    {
        await Should.ThrowAsync<InvalidOperationException>(() =>
            theSource.AddTenantAsync("whatever", "Host=elsewhere;Database=other"));
    }

    [Fact]
    public async Task refresh_and_enumerate_active_tenants()
    {
        var tenant = "active_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        await theSource.AddTenantAsync(tenant, CancellationToken.None);

        await theSource.RefreshAsync();
        theSource.AllActiveByTenant().Select(x => x.TenantId).ShouldContain(tenant);

        await theSource.RemoveTenantAsync(tenant);
    }
}

public class conjoined_tenant_registry_with_postgresql : ConjoinedTenantRegistryCompliance
{
    public conjoined_tenant_registry_with_postgresql() : base(DatabaseEngine.PostgreSQL)
    {
    }
}

public class conjoined_tenant_registry_with_sqlserver : ConjoinedTenantRegistryCompliance
{
    public conjoined_tenant_registry_with_sqlserver() : base(DatabaseEngine.SqlServer)
    {
    }
}
