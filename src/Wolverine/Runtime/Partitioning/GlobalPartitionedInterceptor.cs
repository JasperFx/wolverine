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
            if (ShouldIntercept(envelope))
            {
                if (!await TryReRouteAsync(listener, envelope))
                {
                    passThrough.Add(envelope);
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
        if (ShouldIntercept(envelope))
        {
            if (await TryReRouteAsync(listener, envelope))
            {
                return;
            }
        }

        await _inner.ReceivedAsync(listener, envelope);
    }

    private async Task<bool> TryReRouteAsync(IListener listener, Envelope envelope)
    {
        try
        {
            // Ensure message is deserialized before re-publishing
            if (envelope.Message == null)
            {
                var result = await Pipeline.TryDeserializeEnvelope(envelope);
                if (result is not NullContinuation)
                {
                    // Deserialization failed, let the inner receiver handle it
                    // (it will apply normal error handling)
                    return false;
                }
            }

            // Re-route through Wolverine's routing which will hit GlobalPartitionedRoute
            await _messageBus.PublishAsync(envelope.Message!, new DeliveryOptions
            {
                GroupId = envelope.GroupId,
                TenantId = envelope.TenantId
            });
            await listener.CompleteAsync(envelope);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error re-routing globally partitioned message {MessageType}",
                envelope.MessageType ?? envelope.Message?.GetType().Name ?? "unknown");
            await listener.DeferAsync(envelope);
            return true;
        }
    }

    public ValueTask DrainAsync() => _inner.DrainAsync();
    public void Dispose() => _inner.Dispose();

    private bool ShouldIntercept(Envelope envelope)
    {
        // If message is already deserialized, check the Type directly
        if (envelope.Message != null)
        {
            return _topologies.Any(t => t.Matches(envelope.Message.GetType()));
        }

        // For transports that haven't deserialized yet (e.g. Kafka),
        // check by message type name from envelope metadata/headers
        if (envelope.MessageType != null)
        {
            return _topologies.Any(t => t.MatchesByMessageTypeName(envelope.MessageType));
        }

        return false;
    }
}
