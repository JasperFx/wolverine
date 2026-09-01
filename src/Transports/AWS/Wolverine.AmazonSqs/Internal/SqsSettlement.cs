using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;

namespace Wolverine.AmazonSqs.Internal;

/// <summary>
/// GH-4012 item 3. Terminal-failure classification for the SQS delete -- the settle on this transport.
/// </summary>
/// <remarks>
/// <para>GH-4012 item 5 moved the classification itself onto the block as <c>ShouldRetry</c>; this type now
/// owns the predicate and the give-up log rather than a try/catch. Without classification a permanent
/// failure burns the whole retry budget before being dropped. The same
/// reasoning as <c>sendOrDiscardAsync</c> (GH-3926) applies in the other direction: a <see cref="RetryBlock{T}" />
/// retries until it succeeds, so an operation that can never succeed spins the block for nothing.</para>
///
/// <para>SQS classifies better than either sibling. RabbitMQ has to match broker text -- and that needle
/// was silently wrong for a long time -- while Azure Service Bus offers a boolean; SQS raises distinct
/// typed exceptions, so the terminal set is named rather than inferred:</para>
/// <list type="bullet">
/// <item><see cref="ReceiptHandleIsInvalidException"/> -- the handle is not valid, and no later attempt
/// makes it valid.</item>
/// <item><see cref="MessageNotInflightException"/> -- the visibility window already lapsed, so the
/// message is back on the queue and this delete is addressing something nobody holds.</item>
/// <item><see cref="QueueDoesNotExistException"/> -- the queue is gone; retrying cannot bring it back.</item>
/// </list>
///
/// <para>Deliberately NOT swallowing <see cref="RequestThrottledException"/> or <see cref="OverLimitException"/>:
/// those are exactly the transient failures the retry budget exists for.</para>
///
/// <para>Note there is no ack budget here, unlike the Azure Service Bus path. The SQS delete block is keyed
/// on <see cref="Message"/> rather than on the envelope, so <c>Envelope.AckAttempts</c> is not reachable --
/// carrying it would mean rekeying the block, which is GH-4012 item 1's territory rather than item 3's.</para>
/// </remarks>
internal static class SqsSettlement
{
    internal static Task DeleteAsync(IAmazonSQS client, string queueUrl, string receiptHandle,
        CancellationToken token)
    {
        // GH-4012 item 5: no catch here any more. The block's ShouldRetry does the classification and its
        // OnTerminalFailure does the reporting, so this is just the settle.
        return client.DeleteMessageAsync(queueUrl, receiptHandle, token);
    }

    /// <summary>
    /// GH-4012 item 5. Reports a give-up, which the swallow-in-the-callback shape could not: an exception
    /// caught inside the callback is indistinguishable from success at the block's boundary, so the block
    /// could never log it differently.
    /// </summary>
    internal static void LogTerminalDelete(ILogger logger, string queueUrl, Exception e)
    {
        logger.LogInformation(
            "Discarding a terminal SQS delete failure ({Failure}) on {QueueUrl}; the message will be redelivered when its visibility window lapses",
            e.GetType().Name, queueUrl);
    }

    internal static bool IsTerminal(Exception e)
    {
        return e is ReceiptHandleIsInvalidException or MessageNotInflightException or QueueDoesNotExistException;
    }
}
