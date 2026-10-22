using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Wolverine;
using Wolverine.ErrorHandling;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// F19. With <c>InboxStaleTime</c> set, every database's durability agent releases owned Incoming rows whose stale
/// clock has run out, and the durable receiver is supposed to keep restarting the clock of rows it still holds. It
/// only does that when its inbox is an <c>IMessageInboxHeartbeat</c>, and <c>MultiTenantedMessageStore</c> is not
/// one -- so with a database per tenant a handler slower than the stale time would have its row released while it
/// is still running, recovered, and run a second time at once.
/// </summary>
/// <remarks>
/// The same scenario on a store with one database is the control: there the heartbeat runs. With
/// <c>RIG_TENANT_HEARTBEAT=1</c> the rig restarts the clocks itself, standing in for the missing heartbeat.
/// </remarks>
public abstract class InboxHeartbeatCompliance : IAsyncLifetime
{
    protected static readonly TimeSpan StaleTime = 2.Seconds();

    protected readonly DurableSteps the;

    protected InboxHeartbeatCompliance(StoreProfile store, TransportProfile transport, ITestOutputHelper output)
    {
        the = new DurableSteps(store, transport, output)
        {
            Mode = DurabilityMode.Balanced,
            SettleTimeout = 3.Minutes(),
            QuietPeriod = 10.Seconds()
        };

        the.ConfigureNodes(opts =>
        {
            opts.Durability.InboxStaleTime = StaleTime;

            // The stale bump and recovery run on this cadence: fast, so a released row comes back while the first
            // run is still going
            opts.Durability.ScheduledJobPollingTime = 500.Milliseconds();
            opts.Durability.ScheduledJobFirstExecution = 500.Milliseconds();
            opts.OnException<RigFailure>().RetryTimes(3);
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await the.StopRigHeartbeatAsync();
        await the.DisposeAsync();
    }
}

/// <summary>An untenanted message, whose row is in the main database.</summary>
public abstract class MainDatabaseHeartbeatCompliance(StoreProfile store, TransportProfile transport, ITestOutputHelper output)
    : InboxHeartbeatCompliance(store, transport, output)
{
    /// <summary>An untenanted message: its row is in the main database.</summary>
    [BobcatSpec]
    public async Task a_handler_slower_than_the_inbox_stale_time_runs_once()
    {
        await the.ANodeStarts();
        await the.ANodeStarts();
        the.MessagesToSend(3);
        the.MessageIsSlow(2, 20_000);
        the.RestartStaleClocksForTheReceiverIfAsked(StaleTime);

        await the.TheMessagesAreSentWithTheirTenants(1);

        await the.TheDurableInvariantsHold();
    }
}

/// <summary>F19 with a database per tenant: the slow message's row is in its tenant's database.</summary>
public abstract class TenantInboxHeartbeatCompliance(TenancyStoreProfile store, TransportProfile transport, ITestOutputHelper output)
    : InboxHeartbeatCompliance(store, transport, output)
{
    [BobcatSpec]
    public async Task a_tenants_handler_slower_than_the_inbox_stale_time_runs_once()
    {
        await the.ANodeStarts();
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(2);
        the.MessageIsSlow(1, 20_000);
        the.RestartStaleClocksForTheReceiverIfAsked(StaleTime);

        await the.TheMessagesAreSentWithTheirTenants(1);

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }
}

/// <summary>
/// Both: a tenant's message, and an untenanted one in the main database. Not for Marten, whose
/// <c>MultiTenantedDatabases</c> has no database for a message without a tenant (it is dead lettered).
/// </summary>
public abstract class TenantAndMainHeartbeatCompliance(TenancyStoreProfile store, TransportProfile transport, ITestOutputHelper output)
    : TenantInboxHeartbeatCompliance(store, transport, output)
{
    /// <summary>The composite store fronts the main database too, so its rows get no heartbeat either.</summary>
    [BobcatSpec]
    public async Task an_untenanted_handler_slower_than_the_inbox_stale_time_runs_once()
    {
        await the.ANodeStarts();
        await the.ANodeStarts();
        the.MessagesToSend(3);
        the.MessageIsSlow(2, 20_000);
        the.RestartStaleClocksForTheReceiverIfAsked(StaleTime);

        await the.TheMessagesAreSentWithTheirTenants(1);

        await the.TheDurableInvariantsHold();
    }
}

[BobcatFeature("Inbox heartbeat (F19): PostgreSQL, one database, over a durable local queue (control)")]
public class heartbeat_postgresql_local(ITestOutputHelper output)
    : MainDatabaseHeartbeatCompliance(new PostgresqlStore(), new LocalQueueTransport(), output);

[BobcatFeature("Inbox heartbeat (F19): PostgreSQL, a database per tenant, over a durable local queue")]
public class heartbeat_tenancy_postgresql_local(ITestOutputHelper output)
    : TenantAndMainHeartbeatCompliance(new PostgresqlTenancyStore(), new LocalQueueTransport(), output);

[BobcatFeature("Inbox heartbeat (F19): PostgreSQL, a database per tenant, over RabbitMQ")]
public class heartbeat_tenancy_postgresql_rabbitmq(ITestOutputHelper output)
    : TenantAndMainHeartbeatCompliance(new PostgresqlTenancyStore(), new RabbitMqTransport(), output);

[BobcatFeature("Inbox heartbeat (F19): SQL Server, a database per tenant, over a durable local queue")]
public class heartbeat_tenancy_sqlserver_local(ITestOutputHelper output)
    : TenantAndMainHeartbeatCompliance(new SqlServerTenancyStore(), new LocalQueueTransport(), output);

[BobcatFeature("Inbox heartbeat (F19): Marten, a database per tenant, over a durable local queue")]
public class heartbeat_tenancy_marten_local(ITestOutputHelper output)
    : TenantInboxHeartbeatCompliance(new MartenTenancyStore(), new LocalQueueTransport(), output);

[BobcatFeature("Inbox heartbeat (F19): EF Core, a database per tenant, over a durable local queue")]
public class heartbeat_tenancy_efcore_local(ITestOutputHelper output)
    : TenantAndMainHeartbeatCompliance(new EfCoreTenancyStore(), new LocalQueueTransport(), output);
