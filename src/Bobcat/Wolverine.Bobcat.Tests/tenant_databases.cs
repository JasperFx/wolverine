using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Polecat;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine.Bobcat.Tests.TenantedActs;
using Wolverine.Marten;
using Wolverine.Polecat;
using PolecatStore = global::Polecat.IDocumentStore;

namespace Wolverine.Bobcat.Tests.TenantDatabases;

// GH-4962 phase 2: on a store that gives each tenant its own database, ThenEventuallyCaused waits on and reads the act's
// tenant's database -- found through IEventStore.FindDatabaseForTenantAsync where the store answers, every database
// where it does not -- and an act with no tenant is looked for in every database. On Marten and Polecat; Wolverine.Fisher
// does not support a database per tenant.

public abstract class DatabasePerTenantHost : IAsyncLifetime
{
    public const string Red = "red";
    public const string Blue = "blue";

    public IHost Host { get; private set; } = null!;

    protected abstract Task CreateTenantDatabasesAsync();

    protected abstract void ConfigureStore(WolverineOptions opts);

    /// <summary>
    /// Append <paramref name="e" /> to a new stream in <paramref name="tenantId" />'s database, recorded with
    /// <paramref name="correlationId" />.
    /// </summary>
    public abstract Task AppendForTenantAsync(string tenantId, string correlationId, Guid streamId, object e);

    public async ValueTask InitializeAsync()
    {
        await CreateTenantDatabasesAsync();

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ShipCrateHandler));
                ConfigureStore(opts);
                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is null) return;

        await Host.StopAsync();
        Host.Dispose();
    }
}

public sealed class MartenDatabasePerTenantActsHost : DatabasePerTenantHost
{
    private string _red = null!;
    private string _blue = null!;

    protected override async Task CreateTenantDatabasesAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        _red = await databaseFor(conn, "bobcat_tenant_red");
        _blue = await databaseFor(conn, "bobcat_tenant_blue");
    }

    private static async Task<string> databaseFor(NpgsqlConnection conn, string name)
    {
        if (!await conn.DatabaseExists(name))
        {
            await new DatabaseSpecification().BuildDatabase(conn, name);
        }

        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Database = name }.ConnectionString;
    }

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
            {
                m.DisableNpgsqlLogging = true;
                m.DatabaseSchemaName = "bobcat_tenant_databases";
                m.Events.MetadataConfig.CorrelationIdEnabled = true;
                m.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddSingleTenantDatabase(_red, Red);
                    tenancy.AddSingleTenantDatabase(_blue, Blue);
                });
            })
            .IntegrateWithWolverine(x =>
            {
                x.MessageStorageSchemaName = "bobcat_tenant_databases_control";
                x.MainDatabaseConnectionString = Servers.PostgresConnectionString;
            });

    public override async Task AppendForTenantAsync(string tenantId, string correlationId, Guid streamId, object e)
    {
        await using var session = Host.Services.GetRequiredService<global::Marten.IDocumentStore>().LightweightSession(tenantId);
        session.CorrelationId = correlationId;
        session.Events.StartStream<Crate>(streamId, e);
        await session.SaveChangesAsync();
    }
}

public sealed class PolecatDatabasePerTenantActsHost : DatabasePerTenantHost
{
    private string _red = null!;
    private string _blue = null!;

    protected override async Task CreateTenantDatabasesAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        _red = await databaseFor(conn, "bobcat_tenant_red");
        _blue = await databaseFor(conn, "bobcat_tenant_blue");
    }

    private static async Task<string> databaseFor(SqlConnection conn, string name)
    {
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT DB_ID(@name)";
            check.Parameters.AddWithValue("@name", name);
            if (await check.ExecuteScalarAsync() is null or DBNull)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{name}]";
                await create.ExecuteNonQueryAsync();
            }
        }

        return new SqlConnectionStringBuilder(Servers.SqlServerConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddPolecat(m =>
            {
                m.ConnectionString = Servers.SqlServerConnectionString;
                m.DatabaseSchemaName = "bobcat_tenant_databases";
                m.Events.EnableCorrelationId = true;
                m.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddTenant(Red, _red);
                    tenancy.AddTenant(Blue, _blue);
                });
            })
            .IntegrateWithWolverine(x => x.MainDatabaseConnectionString = Servers.SqlServerConnectionString);

    public override async Task AppendForTenantAsync(string tenantId, string correlationId, Guid streamId, object e)
    {
        await using var session = Host.Services.GetRequiredService<PolecatStore>().LightweightSession(tenantId);
        session.CorrelationId = correlationId;
        session.Events.StartStream<Crate>(streamId, e);
        await session.SaveChangesAsync();
    }
}

public abstract class tenant_databases<THost>(THost app) : WolverineSpec(app.Host) where THost : DatabasePerTenantHost
{
    [Fact]
    public async Task finds_what_the_act_caused_in_its_tenants_database()
    {
        var theCrate = Guid.CreateVersion7();

        await WhenReceived(new ShipCrate(theCrate), options: new DeliveryOptions { TenantId = DatabasePerTenantHost.Red });

        LastAct.TenantId.ShouldBe(DatabasePerTenantHost.Red);
        await ThenEventuallyCaused(new CrateShipped(theCrate));
    }

    [Fact]
    public async Task does_not_look_in_another_tenants_database()
    {
        Scenario.EventuallyTimeout = TimeSpan.FromMilliseconds(300);
        var theCrate = Guid.CreateVersion7();

        await WhenReceived(new ShipCrate(theCrate), options: new DeliveryOptions { TenantId = DatabasePerTenantHost.Red });

        // A blue event recorded with the act's correlation id, in blue's own database
        var blueCrate = Guid.CreateVersion7();
        await app.AppendForTenantAsync(DatabasePerTenantHost.Blue, LastAct.CorrelationId!, blueCrate, new CrateShipped(blueCrate));

        await ThenEventuallyCaused(new CrateShipped(theCrate));

        var failure = await Should.ThrowAsync<Exception>(() => ThenEventuallyCaused(new CrateShipped(blueCrate)));
        failure.Message.ShouldContain("in tenant 'red''s database");
    }

    [Fact]
    public async Task an_act_with_no_tenant_is_looked_for_in_every_database()
    {
        await WhenTracked("nothing happens", () => Task.CompletedTask);
        LastAct.TenantId.ShouldBeNull();

        // Whatever the act's correlation id turns up, in whichever tenant's database
        var blueCrate = Guid.CreateVersion7();
        await app.AppendForTenantAsync(DatabasePerTenantHost.Blue, LastAct.CorrelationId!, blueCrate, new CrateShipped(blueCrate));

        await ThenEventuallyCaused(new CrateShipped(blueCrate));
    }

    [Fact]
    public async Task says_where_it_looked_when_an_act_with_no_tenant_caused_nothing()
    {
        Scenario.EventuallyTimeout = TimeSpan.FromMilliseconds(300);

        await WhenTracked("nothing happens", () => Task.CompletedTask);

        var failure = await Should.ThrowAsync<Exception>(() => ThenEventuallyCaused<CrateShipped>());
        failure.Message.ShouldContain("across its 2 tenant databases (the act carried no tenant)");
    }
}

public sealed class marten_tenant_databases(MartenDatabasePerTenantActsHost app)
    : tenant_databases<MartenDatabasePerTenantActsHost>(app), IClassFixture<MartenDatabasePerTenantActsHost>;

public sealed class polecat_tenant_databases(PolecatDatabasePerTenantActsHost app)
    : tenant_databases<PolecatDatabasePerTenantActsHost>(app), IClassFixture<PolecatDatabasePerTenantActsHost>;
