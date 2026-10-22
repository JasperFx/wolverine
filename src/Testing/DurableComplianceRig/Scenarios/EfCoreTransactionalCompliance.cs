using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Microsoft.EntityFrameworkCore;
using Wolverine.ErrorHandling;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// The EF Core transactional middleware on a durable endpoint: the inbox row is marked handled inside the handler's
/// own transaction, so what happens when that transaction fails AFTER the mark is the interesting part.
/// </summary>
/// <remarks>
/// F25: <c>EfCoreEnvelopeTransaction.CommitAsync</c> sets <c>Envelope.Status = Handled</c> once its in-transaction
/// mark has run, before its own <c>SaveChangesAsync</c> and the commit. If either then fails, the transaction rolls
/// the mark back, but <c>DurableReceiver</c> sees Handled and skips the mark a Discard would otherwise make. Note the
/// handler's own writes are saved by the middleware's earlier <c>SaveChangesAsync</c>, so a failing write of the
/// handler's (<see cref="EfFault.BreakSaveChanges" />) fails BEFORE the mark; only a failure in the commit itself
/// (<see cref="EfFault.FailCommitDiscarded" />) reaches the window.
/// </remarks>
public abstract class EfCoreTransactionalCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected EfCoreTransactionalCompliance(TransportProfile transport, ITestOutputHelper output)
    {
        the = new DurableSteps(new EfCoreStore(), transport, output);
        EfScript.Reset();

        the.ConfigureNodes(opts =>
        {
            opts.OnException<DbUpdateException>().Discard();
            opts.OnException<RigCommitFailure>().Discard();
            opts.OnException<RigFailure>().RetryTimes(3);
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        EfScript.Reset();
        return the.DisposeAsync();
    }

    [BobcatSpec]
    public async Task the_inbox_row_is_marked_handled_inside_the_ef_core_transaction()
    {
        await the.ANodeStarts();
        the.TheInboxIsObservedAtCommit();
        the.MessagesToSend(10);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
        the.InboxRowsWereHandledInsideTheTransaction();
    }

    [BobcatSpec]
    public async Task a_message_whose_save_changes_fails_is_discarded_and_settled()
    {
        the.SettleTimeout = 30.Seconds();

        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsInSaveChangesAndIsDiscarded(2);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_message_whose_commit_fails_is_discarded_and_settled()
    {
        the.SettleTimeout = 30.Seconds();

        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsAtCommitAndIsDiscarded(2);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
        the.LogEfCommitFailures();
    }

    /// <summary>
    /// The second half of F25: the row the failed commit stranded is still Incoming and owned by the node, so a
    /// restart recovers it and runs the discarded message again.
    /// </summary>
    [BobcatSpec]
    public async Task a_discarded_commit_failure_is_not_run_again_after_a_restart()
    {
        the.SettleTimeout = 30.Seconds();

        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsAtCommitOnceAndIsDiscarded(2);

        await the.TheMessagesAreSent(1);
        await the.MessageHasStarted(2);
        await the.MessageHasCommitted(5);
        await the.NodeStops(1);
        await the.ANodeStarts();

        await the.TheDurableInvariantsHold();
        the.LogEfCommitFailures();
    }

    [BobcatSpec]
    public async Task a_message_whose_commit_always_fails_is_dead_lettered_and_settled()
    {
        the.SettleTimeout = 30.Seconds();

        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsAtCommitAndIsDeadLettered(2);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_message_whose_commit_fails_once_is_retried_and_commits_once()
    {
        the.SettleTimeout = 30.Seconds();

        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsAtCommitFirst(2, 1);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }
}

[BobcatFeature("EF Core transactions: a durable local queue")]
public class efcore_transactions_local(ITestOutputHelper output)
    : EfCoreTransactionalCompliance(new LocalQueueTransport(), output);

[BobcatFeature("EF Core transactions: a SQL Server queue")]
public class efcore_transactions_database_queue(ITestOutputHelper output)
    : EfCoreTransactionalCompliance(new DatabaseQueueTransport(), output);

[BobcatFeature("EF Core transactions: durable RabbitMQ")]
public class efcore_transactions_rabbitmq(ITestOutputHelper output)
    : EfCoreTransactionalCompliance(new RabbitMqTransport(), output);

[BobcatFeature("EF Core transactions: durable Amazon SQS")]
public class efcore_transactions_sqs(ITestOutputHelper output)
    : EfCoreTransactionalCompliance(new SqsTransport(), output);
