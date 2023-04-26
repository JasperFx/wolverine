using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Exceptions;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqChannelCallback : IChannelCallback, IDisposable
{
    private readonly RetryBlock<RabbitMqEnvelope> _complete;
    private readonly RetryBlock<RabbitMqEnvelope> _defer;
    private readonly ILogger _logger;

    internal RabbitMqChannelCallback(ILogger logger, CancellationToken cancellationToken)
    {
        _logger = logger;
        _complete = new RetryBlock<RabbitMqEnvelope>((e, _) =>
        {
            // AlreadyClosedException: Already closed: The AMQP operation was interrupted: AMQP close-reason, initiated by Peer, code=406, text='PRECONDITION_FAILED - unknown delivery tag 1', classId=60, methodId=80

            try
            {
                e.Complete();
            }
            catch (AlreadyClosedException exception)
            {
                if (exception.Message.Contains("'PRECONDITION_FAILED - unknown delivery tag'"))
                {
                    logger.LogInformation("Encountered an unknown delivery tag, discarding the envelope");
                    return Task.CompletedTask;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(e.ToString());
                throw;
            }

            return Task.CompletedTask;
        }, logger, cancellationToken);

        _defer = new RetryBlock<RabbitMqEnvelope>((e, _) => e.DeferAsync().AsTask(), logger, cancellationToken);
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is RabbitMqEnvelope e)
        {
            return new ValueTask(_complete.PostAsync(e));
        }

        _logger.LogDebug("Attempting to complete and ack a message to a Rabbit MQ queue, but envelope {Id} is not a RabbitMqEnvelope", envelope.Id);

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is RabbitMqEnvelope e)
        {
            return new ValueTask(_defer.PostAsync(e));
        }
        
        _logger.LogDebug("Attempting to complete and nack a message to a Rabbit MQ queue, but envelope {Id} is not a RabbitMqEnvelope", envelope.Id);

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _complete.Dispose();
        _defer.Dispose();
    }
}