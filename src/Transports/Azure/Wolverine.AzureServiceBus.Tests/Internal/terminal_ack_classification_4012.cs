using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

/// <summary>
/// GH-4012 item 3. Before this, Azure Service Bus treated every settle failure as transient, so a
/// permanent one burned the whole retry budget before being dropped. Azure Service Bus makes the
/// distinction first class -- <c>ServiceBusException.IsTransient</c> -- so unlike RabbitMQ there is no
/// broker text to string-match, which is worth having: the Rabbit needle was silently wrong for a long
/// time precisely because it was a string.
/// </summary>
public class terminal_ack_classification_4012
{
    private static AzureServiceBusEnvelope envelopeFor(ServiceBusReceiver receiver)
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            lockTokenGuid: Guid.NewGuid());

        return new AzureServiceBusEnvelope(message, receiver);
    }

    /// <summary>
    /// GH-4012 item 5 moved the seam: <c>CompleteAsync</c> no longer swallows, so the failure propagates and
    /// the block's <c>ShouldRetry</c> decides. What counts as terminal is unchanged, and all four listeners
    /// now wire it -- including the two session listeners, which had no classification at all.
    /// </summary>
    [Fact]
    public async Task a_terminal_failure_is_classified_and_propagates_to_the_block()
    {
        var receiver = Substitute.For<ServiceBusReceiver>();
        receiver.CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ServiceBusException("lock is gone", ServiceBusFailureReason.MessageLockLost));

        var envelope = envelopeFor(receiver);

        // The block stops on this rather than the callback hiding it. The delivery is left unsettled, its
        // lock lapses, and the broker redelivers -- the same recovery Rabbit's unknown-tag branch relies on.
        var terminal = await Should.ThrowAsync<ServiceBusException>(() =>
            AzureServiceBusSettlement.CompleteAsync(envelope, 3, NullLogger.Instance, CancellationToken.None));

        AzureServiceBusSettlement.IsTerminal(terminal).ShouldBeTrue();
    }

    [Fact]
    public async Task a_transient_failure_is_not_classified_as_terminal()
    {
        var receiver = Substitute.For<ServiceBusReceiver>();
        receiver.CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy));

        var envelope = envelopeFor(receiver);

        // The whole point of classifying: a transient failure must keep its retries, or the budget this
        // change exists to protect would never be spent on the cases that deserve it.
        var transient = await Should.ThrowAsync<ServiceBusException>(() =>
            AzureServiceBusSettlement.CompleteAsync(envelope, 3, NullLogger.Instance, CancellationToken.None));

        AzureServiceBusSettlement.IsTerminal(transient).ShouldBeFalse();
    }

    /// <summary>
    /// GH-4012 item 5. The classification is only worth anything if the block it guards is actually built
    /// with it, and that wiring is precisely what went missing before: item 3 applied it by routing through
    /// a helper that two of the four listeners never called. Every listener now builds its settle block
    /// through <c>CompleteBlock</c>, so this pins the factory once instead of four copies of an initializer.
    /// </summary>
    [Fact]
    public void the_settle_block_factory_carries_the_classification()
    {
        var block = AzureServiceBusSettlement.CompleteBlock((_, _) => Task.CompletedTask,
            NullLogger.Instance, CancellationToken.None);

        block.ShouldRetry.ShouldNotBeNull("Without this the block retries every failure, which is the pre-GH-4012 behaviour.");

        block.ShouldRetry!(new ServiceBusException("lock is gone", ServiceBusFailureReason.MessageLockLost))
            .ShouldBeFalse("A non-transient failure must end the attempt sequence immediately.");

        block.ShouldRetry(new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy))
            .ShouldBeTrue("A transient failure is exactly what the retry budget exists for.");

        // The capability the swallow-in-the-callback shape could not provide: the block gets to report a
        // give-up rather than having it look identical to a success.
        block.OnTerminalFailure.ShouldNotBeNull();
    }

    [Fact]
    public async Task the_ack_budget_is_shared_across_posts_and_stops_the_broker_round_trips()
    {
        var receiver = Substitute.For<ServiceBusReceiver>();
        receiver.CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy));

        var envelope = envelopeFor(receiver);

        for (var i = 0; i < 3; i++)
        {
            await Should.ThrowAsync<ServiceBusException>(() =>
                AzureServiceBusSettlement.CompleteAsync(envelope, 3, NullLogger.Instance, CancellationToken.None));
        }

        // Budget spent. This call must not reach the broker at all -- that is the difference between
        // three round trips and the nine that stacked RetryBlocks used to produce
        await AzureServiceBusSettlement.CompleteAsync(envelope, 3, NullLogger.Instance, CancellationToken.None);

        await receiver.Received(3)
            .CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>());
    }
}
