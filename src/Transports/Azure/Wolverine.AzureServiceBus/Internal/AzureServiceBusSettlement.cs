using Azure.Messaging.ServiceBus;
using JasperFx.Blocks;
using Microsoft.Extensions.Logging;

namespace Wolverine.AzureServiceBus.Internal;

/// <summary>
/// GH-4012 item 3. The settle path shared by both Azure Service Bus listeners, carrying the two
/// protections RabbitMQ already had and Azure Service Bus did not: the envelope's ack budget, and
/// terminal-failure classification.
/// </summary>
/// <remarks>
/// <para>Without classification a permanent failure burns the whole retry budget before being
/// dropped. Azure Service Bus makes the distinction first class -- <see cref="ServiceBusException.IsTransient"/>
/// -- so unlike RabbitMQ there is no broker text to string-match. That matters: the Rabbit needle was
/// wrong for a long time precisely because it was a string, and nothing failed loudly when it stopped
/// matching.</para>
///
/// <para>The give-up leaves the delivery unsettled: its lock lapses and the broker redelivers -- the same
/// recovery the RabbitMQ unknown-delivery-tag branch relies on. GH-4012 item 5 moved the classification off
/// a <c>catch</c> in this method and onto the block itself as <c>ShouldRetry</c>, so the block can tell a
/// terminal give-up from a success and report it through <c>OnTerminalFailure</c>.</para>
/// </remarks>
internal static class AzureServiceBusSettlement
{
    internal static async Task CompleteAsync(AzureServiceBusEnvelope envelope, int maximumAckAttempts,
        ILogger logger, CancellationToken token)
    {
        if (!envelope.TryRecordAckAttempt(maximumAckAttempts))
        {
            logger.LogWarning(
                "Giving up on completing Azure Service Bus message for envelope {EnvelopeId} after {AckAttempts} attempts; leaving it for broker redelivery",
                envelope.Id, envelope.AckAttempts);
            return;
        }

        // GH-4012 item 5: no catch here any more. The block's ShouldRetry classifies and its
        // OnTerminalFailure reports, so this is the budget check plus the settle.
        await envelope.CompleteAsync(token);
    }

    /// <summary>
    /// GH-4012 item 5. The one place a settle block is built, so the classification cannot be left off one.
    ///
    /// <para>
    /// That is not hypothetical. Item 3 applied the classification by routing callbacks through
    /// <see cref="CompleteAsync"/>, and two of the four listeners -- both session listeners -- never called
    /// it, so they kept burning the full retry budget on failures that could never succeed. A factory can be
    /// used wrongly only by not using it, which is visible at the call site; a helper can be forgotten
    /// silently.
    /// </para>
    /// </summary>
    internal static RetryBlock<AzureServiceBusEnvelope> CompleteBlock(
        Func<AzureServiceBusEnvelope, CancellationToken, Task> settle, ILogger logger, CancellationToken token)
    {
        return new RetryBlock<AzureServiceBusEnvelope>((e, c) => settle(e, c), logger, token)
        {
            ShouldRetry = e => !IsTerminal(e),
            OnTerminalFailure = (envelope, e) =>
            {
                LogTerminalSettle(logger, envelope, e);
                return Task.CompletedTask;
            }
        };
    }

    /// <summary>
    /// GH-4012 item 5. Azure Service Bus makes the distinction first class, so unlike RabbitMQ there is no
    /// broker text to string-match. <c>MessageLockLost</c> is the common terminal case: the lock is gone, so
    /// no later attempt on any receiver can settle this delivery, and retrying only delays the redelivery
    /// that is already coming.
    /// </summary>
    internal static bool IsTerminal(Exception e)
    {
        return e is ServiceBusException { IsTransient: false };
    }

    /// <summary>
    /// GH-4012 item 5. Reports a give-up, which the swallow-in-the-callback shape could not: an exception
    /// caught inside the callback is indistinguishable from success at the block's boundary.
    /// </summary>
    internal static void LogTerminalSettle(ILogger logger, Envelope envelope, Exception e)
    {
        logger.LogInformation(
            "Discarding a terminal Azure Service Bus settle failure ({Reason}) for envelope {EnvelopeId}; the broker will redeliver it",
            (e as ServiceBusException)?.Reason.ToString() ?? e.GetType().Name, envelope.Id);
    }
}
