using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Exceptions;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqChannelCallback : IChannelCallback, IDisposable, ISupportDeadLetterQueue
{
    private readonly RetryBlock<RabbitMqEnvelope> _deadLetterQueue;

    // Sample broker text:
    //   Already closed: The AMQP operation was interrupted: AMQP close-reason, initiated by Peer,
    //   code=406, text='PRECONDITION_FAILED - unknown delivery tag 1', classId=60, methodId=80
    //
    // NOTE the tag number sits INSIDE the quotes. Earlier versions of this check looked for
    // "'PRECONDITION_FAILED - unknown delivery tag'" with a trailing apostrophe, which never matched
    // any real broker message -- so in moveToErrorQueueAsync the exception was rethrown and retried
    // three times before being discarded. Match without the closing quote.
    private const string UnknownDeliveryTag = "PRECONDITION_FAILED - unknown delivery tag";

    private static bool isUnknownDeliveryTag(AlreadyClosedException exception)
    {
        return exception.Message.Contains(UnknownDeliveryTag);
    }

    internal RabbitMqChannelCallback(ILogger logger, CancellationToken cancellationToken)
    {
        Logger = logger;
        Complete = new RetryBlock<RabbitMqEnvelope>(async (e, _) =>
        {
            try
            {
                await e.CompleteAsync();
            }
            catch (AlreadyClosedException exception)
            {
                // An unknown delivery tag is terminal -- the tag's channel is gone and no retry on any
                // later channel can succeed. Swallowing it here stops the RetryBlock from burning its
                // budget; the broker will redeliver and the durable inbox will deduplicate.
                if (isUnknownDeliveryTag(exception))
                {
                    logger.LogInformation("Encountered an unknown delivery tag, discarding the envelope");
                }
            }
        }, logger, cancellationToken);

        Defer = new RetryBlock<RabbitMqEnvelope>((e, _) => e.DeferAsync().AsTask(), logger, cancellationToken);
        _deadLetterQueue = new RetryBlock<RabbitMqEnvelope>(moveToErrorQueueAsync, logger, cancellationToken);
    }

    public ILogger Logger { get; }

    public RetryBlock<RabbitMqEnvelope> Complete { get; }

    public RetryBlock<RabbitMqEnvelope> Defer { get; }

    public IHandlerPipeline? Pipeline => null;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is RabbitMqEnvelope e)
        {
            return new ValueTask(Complete.PostAsync(e));
        }

        Logger.LogDebug(
            "Attempting to complete and ack a message to a Rabbit MQ queue, but envelope {Id} is not a RabbitMqEnvelope",
            envelope.Id);

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is RabbitMqEnvelope e)
        {
            return new ValueTask(Defer.PostAsync(e));
        }

        Logger.LogDebug(
            "Attempting to complete and nack a message to a Rabbit MQ queue, but envelope {Id} is not a RabbitMqEnvelope",
            envelope.Id);

        return ValueTask.CompletedTask;
    }

    public virtual void Dispose()
    {
        Complete.Dispose();
        Defer.Dispose();
        _deadLetterQueue.Dispose();
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is RabbitMqEnvelope e)
        {
            return _deadLetterQueue.PostAsync(e);
        }

        Logger.LogDebug(
            "Attempting to move a message to a Rabbit MQ dead letter queue, but envelope {Id} is not a RabbitMqEnvelope",
            envelope.Id);

        return Task.CompletedTask;
    }

    public bool NativeDeadLetterQueueEnabled => true;

    private async Task moveToErrorQueueAsync(RabbitMqEnvelope envelope, CancellationToken token)
    {
        try
        {
            // A null check is not enough -- the tag has to be nacked on the channel it arrived on, or
            // it addresses an unrelated delivery on a rebuilt channel. See RabbitMqListener.CanSettle.
            if (envelope.RabbitMqListener.CanSettle(envelope))
            {
                // Mark as acknowledged before the NACK so that any subsequent
                // CompleteAsync() call is a no-op (prevents double ack/nack)
                envelope.Acknowledged = true;
                envelope.HasBeenAcked = true;
                await envelope.DeliveredOn.BasicNackAsync(envelope.DeliveryTag, false, false, token);
            }
        }
        catch (AlreadyClosedException exception)
        {
            if (isUnknownDeliveryTag(exception))
            {
                Logger.LogInformation("Encountered an unknown delivery tag, discarding the envelope");
                return;
            }

            throw;
        }
    }
}