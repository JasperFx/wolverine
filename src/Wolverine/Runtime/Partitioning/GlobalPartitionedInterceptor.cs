using System.Diagnostics;
using JasperFx.Core;
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
    private readonly IWolverineRuntime _runtime;
    private readonly List<GlobalPartitionedMessageTopology> _topologies;
    private readonly ILogger _logger;

    public GlobalPartitionedInterceptor(IReceiver inner, IWolverineRuntime runtime)
    {
        _inner = inner;
        _runtime = runtime;
        _topologies = runtime.Options.MessagePartitioning.GlobalPartitionedTopologies;
        _logger = runtime.LoggerFactory.CreateLogger<GlobalPartitionedInterceptor>();
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

            var options = new DeliveryOptions
            {
                GroupId = envelope.GroupId,
                TenantId = envelope.TenantId,
                CorrelationId = envelope.CorrelationId,
            };

            var bus = new RouteBus(_runtime, envelope);

            using var activity = StartReRouteActivity(envelope);

            await bus.PublishAsync(envelope.Message!, options);
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

    private static Activity? StartReRouteActivity(Envelope envelope)
    {
        if (envelope.ParentId.IsEmpty())
        {
            return null;
        }

        return WolverineTracing.ActivitySource.StartActivity(
            "wolverine global-partitioning re-route",
            ActivityKind.Internal,
            envelope.ParentId);
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

    private sealed class RouteBus : MessageBus
    {
        public RouteBus(IWolverineRuntime runtime, Envelope inbound) : base(runtime)
        {
            Envelope = inbound;
            CorrelationId = inbound.CorrelationId;
            TenantId = inbound.TenantId;
            UserName = inbound.UserName;
        }
    }
}
