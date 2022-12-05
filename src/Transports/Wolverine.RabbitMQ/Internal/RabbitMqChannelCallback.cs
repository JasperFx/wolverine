using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqChannelCallback : IChannelCallback, IDisposable
{
    private readonly RetryBlock<RabbitMqEnvelope> _complete;
    private readonly RetryBlock<RabbitMqEnvelope> _defer;

    internal RabbitMqChannelCallback(ILogger logger, CancellationToken cancellationToken)
    {
        _complete = new RetryBlock<RabbitMqEnvelope>((e, _) =>
        {
            e.Complete();
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

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is RabbitMqEnvelope e)
        {
            return new ValueTask(_defer.PostAsync(e));
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _complete.Dispose();
        _defer.Dispose();
    }
}