using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Wolverine;
using Wolverine.ErrorHandling;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// One message store (database) per tenant. Three tenants -- red, blue, green -- each with its own database next to
/// the main one. Every message is sent with its tenant id, and on top of the ordinary invariants each scenario judges
/// routing: every effect, inbox row and dead letter must land in its own tenant's database and nowhere else.
/// </summary>
public abstract class TenancyCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected TenancyCompliance(TenancyStoreProfile store, TransportProfile transport, ITestOutputHelper output)
    {
        MartenRigHooks.Reset();
        the = new DurableSteps(store, transport, output);

        // A scheduled retry has to come back well inside the settle timeout
        the.ConfigureNodes(opts => opts.Durability.ScheduledJobPollingTime = 250.Milliseconds());
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await the.RestoreOfflineTenantsAsync();
        MartenRigHooks.Reset();
        await the.DisposeAsync();

        // After an outage SqlClient keeps refusing that connection string for its pool blocking period (5s, then
        // doubling), process wide, so the NEXT scenario's host would fail to start. Only between scenarios: inside
        // one, Wolverine has to recover on its own
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Npgsql.NpgsqlConnection.ClearAllPools();
    }

    [BobcatSpec]
    public async Task every_tenants_messages_settle_in_its_own_database()
    {
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(10);

        await the.TheMessagesAreSentWithTheirTenants(1);

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }

    [BobcatSpec]
    public async Task an_inline_retry_commits_once_in_the_tenants_database()
    {
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(3);
        the.MessageFailsFirst(5, 2);

        await the.TheMessagesAreSentWithTheirTenants(1);

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }

    /// <summary>The retry waits as a Scheduled row; it must be in the tenant's database, and the tenant's durability agent must run it.</summary>
    [BobcatSpec]
    public async Task a_scheduled_retry_waits_in_the_tenants_database_and_runs()
    {
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().ScheduleRetry(3.Seconds(), 3.Seconds()));
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(3);
        the.MessageFailsFirst(8, 1);

        await the.TheMessagesAreSentWithTheirTenants(1);
        await the.MessageIsScheduledInItsTenantsDatabase(8);

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }

    [BobcatSpec]
    public async Task a_dead_letter_lands_in_the_tenants_database()
    {
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(2));
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(3);
        the.MessageAlwaysFails(4);
        the.MessageAlwaysFails(9);

        await the.TheMessagesAreSentWithTheirTenants(1);

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }

    [BobcatSpec]
    public async Task a_failure_after_the_effect_is_written_rolls_back_in_the_tenants_database()
    {
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(3);
        the.MessageFailsBeforeCommit(2, 1);
        the.MessageFailsBeforeCommit(6, 1);

        await the.TheMessagesAreSentWithTheirTenants(1);

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }

    /// <summary>
    /// A node crashes while a tenant's message is running. The row is in the tenant's database; another node has to
    /// recover it from there, run it, and settle it there.
    /// </summary>
    [BobcatSpec]
    public async Task a_message_recovered_after_a_crash_settles_in_its_tenants_database()
    {
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(2);
        the.MessageWaitsForGate(3, "never");
        the.TraceSqlServerBlockingIfAsked(60.Seconds());

        await the.TheMessagesAreSentWithTheirTenants(1);
        await the.MessageHasStarted(3);
        the.TraceInboxRow(3, 90.Seconds());
        the.NodeIsDisposed(1); // a graceful stop, not a crash (see NodeIsDisposed)
        the.TheRunsInFlightDiedWithTheNode(1);
        await the.ANodeStarts();

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }

    /// <summary>
    /// A node sending for a tenant whose database is down: the send itself touches the tenant's database (the durable
    /// local queue stores the row; the RabbitMQ sender too), so the caller is told and nothing is accepted that could
    /// be lost. The other tenants keep flowing. The receiving side of an outage is <see cref="TenantOutageCompliance" />.
    /// </summary>
    [BobcatSpec]
    public async Task a_send_for_an_offline_tenant_is_refused_and_the_others_flow()
    {
        the.SettleTimeout = 2.Minutes();
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(5);

        await the.TenantDatabaseGoesOffline("blue");
        await the.TheMessagesAreSentWithTheirTenants(1);
        the.TheSendsForTenantWereRefused("blue", 5);
        await the.OtherTenantsAreHandledWhileOffline("blue");
        await the.TenantDatabaseComesBack("blue");

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }
}

[BobcatFeature("Tenancy: PostgreSQL, a database per tenant, over a durable local queue")]
public class tenancy_postgresql_local(ITestOutputHelper output)
    : TenancyCompliance(new PostgresqlTenancyStore(), new LocalQueueTransport(), output);

[BobcatFeature("Tenancy: PostgreSQL, a database per tenant, over RabbitMQ")]
public class tenancy_postgresql_rabbitmq(ITestOutputHelper output)
    : TenancyCompliance(new PostgresqlTenancyStore(), new RabbitMqTransport(), output);

[BobcatFeature("Tenancy: SQL Server, a database per tenant, over a durable local queue")]
public class tenancy_sqlserver_local(ITestOutputHelper output)
    : TenancyCompliance(new SqlServerTenancyStore(), new LocalQueueTransport(), output);

[BobcatFeature("Tenancy: SQL Server, a database per tenant, over RabbitMQ")]
public class tenancy_sqlserver_rabbitmq(ITestOutputHelper output)
    : TenancyCompliance(new SqlServerTenancyStore(), new RabbitMqTransport(), output);

[BobcatFeature("Tenancy: Marten, a database per tenant, over a durable local queue")]
public class tenancy_marten_local(ITestOutputHelper output)
    : TenancyCompliance(new MartenTenancyStore(), new LocalQueueTransport(), output);

[BobcatFeature("Tenancy: Marten, a database per tenant, over RabbitMQ")]
public class tenancy_marten_rabbitmq(ITestOutputHelper output)
    : TenancyCompliance(new MartenTenancyStore(), new RabbitMqTransport(), output);

[BobcatFeature("Tenancy: EF Core, a database per tenant, over a durable local queue")]
public class tenancy_efcore_local(ITestOutputHelper output)
    : TenancyCompliance(new EfCoreTenancyStore(), new LocalQueueTransport(), output);

[BobcatFeature("Tenancy: EF Core, a database per tenant, over RabbitMQ")]
public class tenancy_efcore_rabbitmq(ITestOutputHelper output)
    : TenancyCompliance(new EfCoreTenancyStore(), new RabbitMqTransport(), output);
