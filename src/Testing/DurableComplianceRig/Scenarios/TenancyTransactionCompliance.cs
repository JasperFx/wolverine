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
        await the.DisposeAsync();
    }

    [BobcatSpec]
    public async Task a_crash_after_the_tenants_commit_does_not_run_the_message_again()
    {
        await the.ANodeStarts();
        the.MessagesToSendForEachTenant(2);
        the.MessageHoldsAfterCommit(3);

        await the.TheMessagesAreSentWithTheirTenants(1);
        await the.MessageIsHeldAfterCommit(3);
        the.NodeCrashes(1);
        await the.ANodeStarts();

        await the.TheDurableInvariantsHold();
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
    : TenancyTransactionCompliance(new EfCoreTenancyStore(), new LocalQueueTransport(), output);

[BobcatFeature("Tenancy transactions: EF Core, a database per tenant, over RabbitMQ")]
public class tenancy_transactions_efcore_rabbitmq(ITestOutputHelper output)
    : TenancyTransactionCompliance(new EfCoreTenancyStore(), new RabbitMqTransport(), output);
