using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.RabbitMQ;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// <c>AutoApplyTransactions(IdempotencyStyle.X)</c> on endpoints WITHOUT a durable inbox: the EF Core transaction
/// writes a Handled inbox row for the envelope id, so a redelivery of the same envelope is discarded rather than run
/// again. Every spec redelivers a message (same message id, so the same envelope id) and judges exactly-once.
/// </summary>
/// <remarks>
/// A discarded redelivery leaves no trace the checker can wait for, so the quiet period is long enough for it to
/// have been received and refused.
/// </remarks>
public abstract class EfCoreIdempotencyCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected EfCoreIdempotencyCompliance(IdempotencyStyle idempotency, TransportProfile transport, ITestOutputHelper output)
    {
        the = new DurableSteps(new EfCoreStore(idempotency), transport, output);
        EfScript.Reset();

        the.QuietPeriod = 5.Seconds();
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        EfScript.Reset();
        return the.DisposeAsync();
    }

    [BobcatSpec]
    public async Task a_redelivery_after_the_message_committed_is_discarded()
    {
        await the.ANodeStarts();
        the.MessagesToSend(3);

        await the.TheMessagesAreSent(1);
        await the.MessageHasCommitted(2);
        await the.MessageIsDeliveredAgain(2, 1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_redelivery_at_the_same_time_commits_once()
    {
        await the.ANodeStarts();
        the.MessagesToSend(3);
        the.MessageIsSlow(2, 500);

        await the.TheMessagesAreSent(1);
        await the.MessageIsDeliveredAgain(2, 1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_redelivery_after_a_retried_failure_is_discarded()
    {
        await the.ANodeStarts();
        the.TheInboxIsObservedAtCommit();
        the.MessagesToSend(3);
        the.MessageFailsFirst(2, 1);

        await the.TheMessagesAreSent(1);
        await the.MessageHasCommitted(2);
        await the.MessageIsDeliveredAgain(2, 1);

        await the.TheDurableInvariantsHold();
        the.LogInboxStatusAtCommit();
    }

    [BobcatSpec]
    public async Task a_redelivery_after_a_retried_commit_failure_is_discarded()
    {
        await the.ANodeStarts();
        the.TheInboxIsObservedAtCommit();
        the.MessagesToSend(3);
        the.MessageFailsAtCommitFirst(2, 1);

        await the.TheMessagesAreSent(1);
        await the.MessageHasCommitted(2);
        await the.MessageIsDeliveredAgain(2, 1);

        await the.TheDurableInvariantsHold();
        the.LogInboxStatusAtCommit();
    }
}

/// <summary>RabbitMQ with a buffered (in memory) listener: no durable inbox row until the handler's transaction writes one.</summary>
public class BufferedRabbitMqTransport : TransportProfile
{
    public override string Name => "RabbitMQ, buffered";
    public override string Key => "rabbitbuf";

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();
        opts.PublishMessage<RigMessage>().ToRabbitQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.ListenToRabbitQueue(queueName).BufferedInMemory();
    }
}

/// <summary>RabbitMQ with an inline listener: acked after the handler, no durable inbox row until the handler's transaction writes one.</summary>
public class InlineRabbitMqTransport : TransportProfile
{
    public override string Name => "RabbitMQ, inline";
    public override string Key => "rabbitinl";

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();
        opts.PublishMessage<RigMessage>().ToRabbitQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.ListenToRabbitQueue(queueName).ProcessInline();
    }
}

[BobcatFeature("EF Core idempotency: Optimistic on a buffered RabbitMQ listener")]
public class efcore_idempotency_optimistic_buffered(ITestOutputHelper output)
    : EfCoreIdempotencyCompliance(IdempotencyStyle.Optimistic, new BufferedRabbitMqTransport(), output);

[BobcatFeature("EF Core idempotency: Optimistic on an inline RabbitMQ listener")]
public class efcore_idempotency_optimistic_inline(ITestOutputHelper output)
    : EfCoreIdempotencyCompliance(IdempotencyStyle.Optimistic, new InlineRabbitMqTransport(), output);

[BobcatFeature("EF Core idempotency: Eager on a buffered RabbitMQ listener")]
public class efcore_idempotency_eager_buffered(ITestOutputHelper output)
    : EfCoreIdempotencyCompliance(IdempotencyStyle.Eager, new BufferedRabbitMqTransport(), output);

[BobcatFeature("EF Core idempotency: Eager on an inline RabbitMQ listener")]
public class efcore_idempotency_eager_inline(ITestOutputHelper output)
    : EfCoreIdempotencyCompliance(IdempotencyStyle.Eager, new InlineRabbitMqTransport(), output);

[BobcatFeature("EF Core idempotency: Eager on a durable RabbitMQ listener (control)")]
public class efcore_idempotency_eager_durable(ITestOutputHelper output)
    : EfCoreIdempotencyCompliance(IdempotencyStyle.Eager, new RabbitMqTransport(), output);
