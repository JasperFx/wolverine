using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Exceptions;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqChannelCallback : IChannelCallback, IDisposable, ISupportDeadLetterQueue
{
    private readonly RetryBlock<RabbitMqEnvelope> _deadLetterQueue;

    internal RabbitMqChannelCallback(ILogger logger, CancellationToken cancellationToken)
    {
        Logger = logger;
        Complete = new RetryBlock<RabbitMqEnvelope>(async (e, _) =>
        {
            // AlreadyClosedException: Already closed: The AMQP operation was interrupted: AMQP close-reason, initiated by Peer, code=406, text='PRECONDITION_FAILED - unknown delivery tag 1', classId=60, methodId=80

            try
            {
                await e.CompleteAsync();
            }
            catch (AlreadyClosedException exception)
            {
                if (exception.Message.Contains("'PRECONDITION_FAILED - unknown delivery tag'"))
                {
                    logger.LogInformation("Encountered an unknown delivery tag, discarding the envelope");
                }
            }
            
        }, logger, cancellationToken);

        Defer = new RetryBlock<RabbitMqEnvelope>((e, _) => e.DeferAsync().AsTask(), logger, cancellationToken);
        _deadLetterQueue = new RetryBlock<RabbitMqEnvelope>(moveToErrorQueueAsync, logger, cancellationToken);
    }

    public IHandlerPipeline? Pipeline => null;

    public ILogger Logger { get; }

    public RetryBlock<RabbitMqEnvelope> Complete { get; }

    public RetryBlock<RabbitMqEnvelope> Defer { get; }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is RabbitMqEnvelope e)
        {
            return new ValueTask(Complete.PostAsync(e));
        }

        Logger.LogDebug("Attempting to complete and ack a message to a Rabbit MQ queue, but envelope {Id} is not a RabbitMqEnvelope", envelope.Id);

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is RabbitMqEnvelope e)
        {
            return new ValueTask(Defer.PostAsync(e));
        }

        Logger.LogDebug("Attempting to complete and nack a message to a Rabbit MQ queue, but envelope {Id} is not a RabbitMqEnvelope", envelope.Id);

        return ValueTask.CompletedTask;
    }

    private async Task moveToErrorQueueAsync(RabbitMqEnvelope envelope, CancellationToken token)
    {
        try
        {
            if(envelope.RabbitMqListener.Channel is not null)
                await envelope.RabbitMqListener.Channel.BasicNackAsync(envelope.DeliveryTag, false, false, token);
        }
        catch (AlreadyClosedException exception)
        {
            if (exception.Message.Contains("'PRECONDITION_FAILED - unknown delivery tag'"))
            {
                Logger.LogInformation("Encountered an unknown delivery tag, discarding the envelope");
                return;
            }

            throw;
        }

        return;
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is RabbitMqEnvelope e)
        {
            return _deadLetterQueue.PostAsync(e);
        }

        Logger.LogDebug("Attempting to move a message to a Rabbit MQ dead letter queue, but envelope {Id} is not a RabbitMqEnvelope", envelope.Id);

        return Task.CompletedTask;
    }

    public bool NativeDeadLetterQueueEnabled => true;

    public virtual void Dispose()
    {
        Complete.Dispose();
        Defer.Dispose();
        _deadLetterQueue.Dispose();
    }
}
