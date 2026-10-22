using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Wolverine.ErrorHandling;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// The transactional boundary per tenant on the ExactlyOnce stores (Marten, EF Core): the inbox row is marked
/// handled inside the handler's own transaction, and that transaction is on the TENANT's database. A crash after
/// the commit and before the receiver settles the row must not run the message again.
/// </summary>
public abstract class TenancyTransactionCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected TenancyTransactionCompliance(TenancyStoreProfile store, TransportProfile transport, ITestOutputHelper output)
    {
        MartenRigHooks.Reset();
        the = new DurableSteps(store, transport, output);
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        MartenRigHooks.Reset();
        EfScript.Reset();
        await the.DisposeAsync();
    }

    [BobcatSpec]
    public async Task a_crash_after_the_tenants_commit_does_not_run_the_message_again()
    {
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(2);
        the.MessageHoldsAfterCommit(3);
        the.TraceSqlServerBlockingIfAsked(90.Seconds());

        await the.TheMessagesAreSentWithTheirTenants(1);
        await the.MessageIsHeldAfterCommit(3);
        the.TraceInboxRow(3, 90.Seconds());
        the.NodeIsDisposed(1); // a graceful stop, not a crash (see NodeIsDisposed)
        await the.ANodeStarts();

        await the.TheDurableInvariantsHold();
        await the.EverythingIsInItsTenantsDatabase();
    }
}

/// <summary>
/// The EF Core twin adds the direct observation: the interceptor reads the inbox row on the committing transaction,
/// which is the tenant database's own, just before the commit. ExactlyOnce needs it to read Handled there.
/// </summary>
public abstract class EfCoreTenancyTransactionCompliance(TransportProfile transport, ITestOutputHelper output)
    : TenancyTransactionCompliance(new EfCoreTenancyStore(), transport, output)
{
    [BobcatSpec]
    public async Task the_inbox_row_is_marked_handled_inside_the_tenants_ef_core_transaction()
    {
        await the.ANodeStarts();
        the.TheInboxIsObservedAtCommit();
        the.MessagesToSendForEachTenant(3);

        await the.TheMessagesAreSentWithTheirTenants(1);

        await the.TheDurableInvariantsHold();
        the.InboxRowsWereHandledInsideTheTransaction();
        await the.EverythingIsInItsTenantsDatabase();
    }
}

[BobcatFeature("Tenancy transactions: Marten, a database per tenant, over a durable local queue")]
public class tenancy_transactions_marten_local(ITestOutputHelper output)
    : TenancyTransactionCompliance(new MartenTenancyStore(), new LocalQueueTransport(), output);

[BobcatFeature("Tenancy transactions: Marten, a database per tenant, over RabbitMQ")]
public class tenancy_transactions_marten_rabbitmq(ITestOutputHelper output)
    : TenancyTransactionCompliance(new MartenTenancyStore(), new RabbitMqTransport(), output);

[BobcatFeature("Tenancy transactions: EF Core, a database per tenant, over a durable local queue")]
public class tenancy_transactions_efcore_local(ITestOutputHelper output)
    : EfCoreTenancyTransactionCompliance(new LocalQueueTransport(), output);

[BobcatFeature("Tenancy transactions: EF Core, a database per tenant, over RabbitMQ")]
public class tenancy_transactions_efcore_rabbitmq(ITestOutputHelper output)
    : EfCoreTenancyTransactionCompliance(new RabbitMqTransport(), output);

/// <summary>
/// The control for the per-tenant crash scenario: the same crash on EF Core with ONE database
/// (<see cref="EfCoreHoldStore" />, <c>AddDbContextWithWolverineIntegration</c>), whose middleware commits through
/// <c>EfCoreEnvelopeTransaction</c> and marks the row inside the transaction.
/// </summary>
public abstract class EfCoreCrashControl : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected EfCoreCrashControl(TransportProfile transport, ITestOutputHelper output)
    {
        MartenRigHooks.Reset();
        the = new DurableSteps(new EfCoreHoldStore(), transport, output);
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        MartenRigHooks.Reset();
        await the.DisposeAsync();
    }

    [BobcatSpec]
    public async Task a_crash_after_the_commit_does_not_run_the_message_again()
    {
        await the.ANodeStarts();
        the.MessagesToSend(6);
        the.MessageHoldsAfterCommit(3);

        await the.TheMessagesAreSent(1);
        await the.MessageIsHeldAfterCommit(3);
        the.TraceInboxRow(3, 90.Seconds());
        the.NodeIsDisposed(1); // a graceful stop, not a crash (see NodeIsDisposed)
        await the.ANodeStarts();

        await the.TheDurableInvariantsHold();
    }
}

[BobcatFeature("Tenancy transactions control: EF Core, one database, over a durable local queue")]
public class tenancy_control_efcore_local(ITestOutputHelper output) : EfCoreCrashControl(new LocalQueueTransport(), output);
