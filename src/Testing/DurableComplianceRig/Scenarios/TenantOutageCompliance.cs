using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Wolverine.ErrorHandling;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// GH-4435/4658/4659, the receiving side: a tenant's database is down while its messages ARRIVE from the broker. The
/// listener serves every tenant, so it must keep running for the others, and the stranded tenant's messages must be
/// deferred back to the broker rather than lost, and handled once the database is back. The messages come from a
/// sender with no message store: a node's own send for an offline tenant is refused before it reaches the broker.
/// </summary>
public abstract class TenantOutageCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected TenantOutageCompliance(TenancyStoreProfile store, ITestOutputHelper output)
    {
        MartenRigHooks.Reset();
        the = new DurableSteps(store, new RabbitMqTransport(), output) { SettleTimeout = 2.Minutes() };
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await the.RestoreOfflineTenantsAsync();
        MartenRigHooks.Reset();
        await the.DisposeAsync();

        // See TenancyCompliance.DisposeAsync
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Npgsql.NpgsqlConnection.ClearAllPools();
    }

    [BobcatSpec]
    public async Task one_tenant_database_offline_stops_nobody_else_and_loses_nothing()
    {
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(5);

        await the.TenantDatabaseGoesOffline("blue");
        await the.TheMessagesAreSentFromABareSender();
        await the.OtherTenantsAreHandledWhileOffline("blue");
        await the.TenantDatabaseComesBack("blue");

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }
}

[BobcatFeature("Tenant outage: PostgreSQL, a database per tenant, over RabbitMQ")]
public class outage_postgresql_rabbitmq(ITestOutputHelper output) : TenantOutageCompliance(new PostgresqlTenancyStore(), output);

[BobcatFeature("Tenant outage: SQL Server, a database per tenant, over RabbitMQ")]
public class outage_sqlserver_rabbitmq(ITestOutputHelper output) : TenantOutageCompliance(new SqlServerTenancyStore(), output);

[BobcatFeature("Tenant outage: Marten, a database per tenant, over RabbitMQ")]
public class outage_marten_rabbitmq(ITestOutputHelper output) : TenantOutageCompliance(new MartenTenancyStore(), output);

[BobcatFeature("Tenant outage: EF Core, a database per tenant, over RabbitMQ")]
public class outage_efcore_rabbitmq(ITestOutputHelper output) : TenantOutageCompliance(new EfCoreTenancyStore(), output);
