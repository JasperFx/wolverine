using Microsoft.Extensions.Logging;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.Runtime.Partitioning;

/// <summary>
/// Wraps an IReceiver on non-paired external listeners to intercept messages
/// that match global partitioning rules and re-route them through Wolverine's
/// message routing for proper partition assignment.
/// </summary>
internal class GlobalPartitionedInterceptor : IReceiver
{
    private readonly IReceiver _inner;
    private readonly IMessageBus _messageBus;
    private readonly List<GlobalPartitionedMessageTopology> _topologies;
    private readonly ILogger _logger;

    public GlobalPartitionedInterceptor(IReceiver inner, IMessageBus messageBus,
        List<GlobalPartitionedMessageTopology> topologies, ILogger logger)
    {
        _inner = inner;
        _messageBus = messageBus;
        _topologies = topologies;
        _logger = logger;
    }

    public IHandlerPipeline Pipeline => _inner.Pipeline;

    public async ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        var passThrough = new List<Envelope>();

        foreach (var envelope in messages)
        {
            if (envelope.Message != null && ShouldIntercept(envelope.Message.GetType()))
            {
                // Re-route through Wolverine's routing which will hit GlobalPartitionedRoute
                try
                {
                    await _messageBus.PublishAsync(envelope.Message, new DeliveryOptions
                    {
                        GroupId = envelope.GroupId,
                        TenantId = envelope.TenantId
                    });
                    await listener.CompleteAsync(envelope);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error re-routing globally partitioned message {MessageType}", envelope.Message.GetType().Name);
                    await listener.DeferAsync(envelope);
                }
            }
            else
            {
                passThrough.Add(envelope);
            }
        }

        if (passThrough.Count > 0)
        {
            await _inner.ReceivedAsync(listener, passThrough.ToArray());
        }
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        if (envelope.Message != null && ShouldIntercept(envelope.Message.GetType()))
        {
            try
            {
                await _messageBus.PublishAsync(envelope.Message, new DeliveryOptions
                {
                    GroupId = envelope.GroupId,
                    TenantId = envelope.TenantId
                });
                await listener.CompleteAsync(envelope);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error re-routing globally partitioned message {MessageType}", envelope.Message.GetType().Name);
                await listener.DeferAsync(envelope);
            }
            return;
        }

        await _inner.ReceivedAsync(listener, envelope);
    }

    public ValueTask DrainAsync() => _inner.DrainAsync();
    public void Dispose() => _inner.Dispose();

    private bool ShouldIntercept(Type messageType)
    {
        return _topologies.Any(t => t.Matches(messageType));
    }
}
