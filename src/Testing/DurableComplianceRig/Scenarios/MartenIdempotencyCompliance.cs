using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// Marten's handler idempotency, <c>AutoApplyTransactions(IdempotencyStyle)</c>. Before the handler runs, the
/// Marten session frame calls <c>MessageContext.AssertEagerIdempotencyAsync</c>, which inserts a Handled inbox row
/// for the envelope and commits it (<c>MartenEnvelopeTransaction.TryMakeEagerIdempotencyCheckAsync</c>); a second
/// delivery of the same envelope id hits the primary key and is discarded. Documented for Inline and Buffered
/// endpoints; on a durable endpoint the check returns early because the inbox already holds the row.
/// </summary>
/// <remarks>
/// <c>IdempotencyStyle.Optimistic</c> is documented as behaving exactly like Eager since 5.4.1, so both run here.
/// Every spec counts the deliveries that reached the handler chain (<see cref="CountDeliveriesPolicy" />): without
/// that, a duplicate the transport never delivered would pass for one the check dropped.
/// </remarks>
public abstract class MartenIdempotencyCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    // A durable endpoint's inbox insert drops a redelivered id before the handler chain ever sees it
    private readonly int _deliveriesOfADuplicate;

    protected MartenIdempotencyCompliance(IdempotencyStyle style, TransportProfile transport, ITestOutputHelper output)
    {
        _deliveriesOfADuplicate =
            transport is LocalQueueTransport or DatabaseQueueTransport or RabbitMqTransport or SqsTransport ? 1 : 2;

        MartenRigHooks.Reset();
        the = new DurableSteps(new MartenStore(style), transport, output);
        the.ConfigureNodes(opts => opts.Policies.Add<CountDeliveriesPolicy>());
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        MartenRigHooks.Reset();
        await the.DisposeAsync();
    }

    private void retriesInline() => the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));

    [BobcatSpec]
    public async Task a_duplicate_delivered_alongside_the_original_commits_one_effect()
    {
        retriesInline();
        await the.ANodeStarts();
        the.MessagesToSend(5);

        await the.TheMessagesAreSent(1);
        await the.MessageIsSentAgain(2, 1);

        await the.TheDurableInvariantsHold();
        the.MessageReachedTheChain(2, _deliveriesOfADuplicate);
    }

    [BobcatSpec]
    public async Task a_duplicate_delivered_after_the_original_committed_is_dropped()
    {
        retriesInline();
        await the.ANodeStarts();
        the.MessagesToSend(3);

        await the.TheMessagesAreSent(1);
        await the.MessageHasCommitted(1);
        await the.MessageIsSentAgain(1, 1);

        await the.TheDurableInvariantsHold();
        the.MessageReachedTheChain(1, _deliveriesOfADuplicate);
    }

    [BobcatSpec]
    public async Task a_failure_after_the_effect_is_written_then_a_success_commits_once()
    {
        retriesInline();
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsBeforeCommit(2, 1);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_message_requeued_after_a_failure_is_not_mistaken_for_its_own_duplicate()
    {
        // Requeue hands the message back to the transport. The eager check already committed a Handled row for
        // its id on the first attempt, so whatever comes back has to get past that row
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().Requeue(3));
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsFirst(2, 1);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_message_that_always_fails_is_dead_lettered_with_no_effect()
    {
        retriesInline();
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageAlwaysFails(4);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }
}

[BobcatFeature("Marten eager idempotency: a buffered local queue")]
public class marten_eager_idempotency_buffered_local(ITestOutputHelper output)
    : MartenIdempotencyCompliance(IdempotencyStyle.Eager, new BufferedLocalQueueTransport(), output);

[BobcatFeature("Marten eager idempotency: an inline RabbitMQ listener")]
public class marten_eager_idempotency_inline_rabbitmq(ITestOutputHelper output)
    : MartenIdempotencyCompliance(IdempotencyStyle.Eager, new InlineRabbitMqTransport(), output);

[BobcatFeature("Marten eager idempotency: a buffered RabbitMQ listener")]
public class marten_eager_idempotency_buffered_rabbitmq(ITestOutputHelper output)
    : MartenIdempotencyCompliance(IdempotencyStyle.Eager, new BufferedRabbitMqTransport(), output);

[BobcatFeature("Marten optimistic idempotency: a buffered local queue")]
public class marten_optimistic_idempotency_buffered_local(ITestOutputHelper output)
    : MartenIdempotencyCompliance(IdempotencyStyle.Optimistic, new BufferedLocalQueueTransport(), output);

[BobcatFeature("Marten optimistic idempotency: an inline RabbitMQ listener")]
public class marten_optimistic_idempotency_inline_rabbitmq(ITestOutputHelper output)
    : MartenIdempotencyCompliance(IdempotencyStyle.Optimistic, new InlineRabbitMqTransport(), output);

/// <summary>
/// The idempotency styles are documented for Inline and Buffered endpoints only, but <c>AutoApplyTransactions</c>
/// applies one to every chain. On a durable endpoint the check must be a no-op: it returns early because
/// <c>WasPersistedInInbox</c> is already set (<c>DurableReceiver.EnqueueAsync</c>, <c>DurableLocalQueue</c>). These
/// two features pin that down, the database queue in particular, whose inbox row is written by the dequeue rather
/// than by the receiver.
/// </summary>
[BobcatFeature("Marten eager idempotency: a durable PostgreSQL queue")]
public class marten_eager_idempotency_database_queue(ITestOutputHelper output)
    : MartenIdempotencyCompliance(IdempotencyStyle.Eager, new DatabaseQueueTransport(), output);

[BobcatFeature("Marten eager idempotency: a durable local queue")]
public class marten_eager_idempotency_durable_local(ITestOutputHelper output)
    : MartenIdempotencyCompliance(IdempotencyStyle.Eager, new LocalQueueTransport(), output);
