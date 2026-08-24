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

    [Fact]
    public async Task a_terminal_failure_is_swallowed_so_the_retry_block_stops()
    {
        var receiver = Substitute.For<ServiceBusReceiver>();
        receiver.CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ServiceBusException("lock is gone", ServiceBusFailureReason.MessageLockLost));

        var envelope = envelopeFor(receiver);

        // Swallowing is what stops the RetryBlock. The delivery is left unsettled, its lock lapses,
        // and the broker redelivers -- the same recovery Rabbit's unknown-delivery-tag branch relies on
        await AzureServiceBusSettlement.CompleteAsync(envelope, 3, NullLogger.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task a_transient_failure_still_propagates_so_the_retry_block_retries()
    {
        var receiver = Substitute.For<ServiceBusReceiver>();
        receiver.CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy));

        var envelope = envelopeFor(receiver);

        // The whole point of classifying: a transient failure must NOT be swallowed, or the retry
        // budget this change exists to protect would never be spent on the cases that deserve it
        await Should.ThrowAsync<ServiceBusException>(() =>
            AzureServiceBusSettlement.CompleteAsync(envelope, 3, NullLogger.Instance, CancellationToken.None));
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
