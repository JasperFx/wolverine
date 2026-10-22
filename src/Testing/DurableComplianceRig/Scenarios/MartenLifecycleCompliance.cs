using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Wolverine.ErrorHandling;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// Marten on a durable endpoint, at the edges of a handler's transaction: a handler that reschedules itself, and a
/// crash in the window between the Marten commit and the durable receiver settling the row.
/// </summary>
public abstract class MartenLifecycleCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected MartenLifecycleCompliance(TransportProfile transport, ITestOutputHelper output)
    {
        MartenRigHooks.Reset();
        the = new DurableSteps(new MartenStore(), transport, output);
        the.ConfigureNodes(opts =>
        {
            opts.OnException<RigFailure>().RetryTimes(3);

            // A rescheduled row has to come back well inside the settle timeout
            opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        MartenRigHooks.Reset();
        await the.DisposeAsync();
    }

    /// <summary>
    /// F13. <c>ReScheduleAsync</c> moves the row to Scheduled, then the handler returns normally: the Marten commit
    /// marks the row handled in its own transaction and the receiver completes it again. Either one overwriting
    /// Scheduled with Handled loses the rescheduled run.
    /// </summary>
    [BobcatSpec]
    public async Task a_handler_that_reschedules_itself_and_returns_normally_runs_again()
    {
        await the.ANodeStarts();
        the.MessagesToSend(3);
        the.MessageReschedulesItself(2);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    /// <summary>
    /// The decisive check of ExactlyOnce: the row is marked handled inside the Marten transaction, so a crash after
    /// the commit and before the receiver's own completion must not run the message a second time.
    /// </summary>
    [BobcatSpec]
    public async Task a_crash_after_the_commit_and_before_the_row_is_settled_does_not_run_the_message_again()
    {
        await the.ANodeStarts();
        the.MessagesToSend(3);
        the.MessageHoldsAfterCommit(2);

        await the.TheMessagesAreSent(1);
        await the.MessageIsHeldAfterCommit(2);
        the.NodeCrashes(1);
        await the.ANodeStarts();

        await the.TheDurableInvariantsHold();
    }
}

[BobcatFeature("Marten lifecycle: a durable local queue")]
public class marten_lifecycle_local(ITestOutputHelper output)
    : MartenLifecycleCompliance(new LocalQueueTransport(), output);

[BobcatFeature("Marten lifecycle: a PostgreSQL queue")]
public class marten_lifecycle_database_queue(ITestOutputHelper output)
    : MartenLifecycleCompliance(new DatabaseQueueTransport(), output);

[BobcatFeature("Marten lifecycle: RabbitMQ")]
public class marten_lifecycle_rabbitmq(ITestOutputHelper output)
    : MartenLifecycleCompliance(new RabbitMqTransport(), output);

[BobcatFeature("Marten lifecycle: Amazon SQS")]
public class marten_lifecycle_sqs(ITestOutputHelper output)
    : MartenLifecycleCompliance(new SqsTransport(), output);
