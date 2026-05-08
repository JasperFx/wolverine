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

            // The bus is seeded with the inbound envelope so PropagateHeadersRule and
            // every other IMetadataRule.ApplyCorrelation impl can read the originator
            // when they enrich each outbound envelope — same shape as the publish path
            // inside a regular handler. Context-correlation field copying (CorrelationId,
            // ConversationId, TenantId, UserName, ParentId, SagaId) happens in the bus's
            // overridden TrackEnvelopeCorrelation via Envelope.CopyContextCorrelationFrom,
            // so we don't need to re-state any of those fields on DeliveryOptions here —
            // GroupId is the only piece the routing layer itself needs to read.
            var options = new DeliveryOptions
            {
                GroupId = envelope.GroupId,
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

    /// <summary>
    /// MessageBus subclass used only by the global-partitioning re-route path.
    /// Two responsibilities:
    /// <list type="number">
    ///   <item>Seed <see cref="MessageContext.Envelope"/> with the inbound envelope so
    ///   <see cref="IEnvelopeRule.ApplyCorrelation"/> implementations such as
    ///   <c>PropagateHeadersRule</c> see <c>originator.Envelope</c> when they enrich
    ///   the outbound envelopes — without this seed those rules short-circuit and the
    ///   <c>PropagateIncomingHeadersToOutgoing</c> allowlist is silently ignored for
    ///   globally-partitioned messages.</item>
    ///   <item>Override <see cref="MessageBus.TrackEnvelopeCorrelation"/> so each
    ///   re-routed outbound envelope inherits the inbound's full context-correlation
    ///   set (<c>CorrelationId</c>, <c>ConversationId</c>, <c>TenantId</c>,
    ///   <c>UserName</c>, <c>ParentId</c>, <c>SagaId</c>) via
    ///   <see cref="Envelope.CopyContextCorrelationFrom"/> — the same forwarding
    ///   semantics already used by <c>ScheduledSendEnvelopeHandler</c> for unwrapped
    ///   scheduled sends and by <c>TrackedSession.ReplayAll</c> for replayed envelopes.
    ///   The base <c>TrackEnvelopeCorrelation</c> only pulls <c>CorrelationId</c> /
    ///   <c>TenantId</c> / <c>UserName</c> from the bus's own properties and writes a
    ///   fresh <c>ParentId</c> from <c>Activity.Current</c>, which is the wrong shape
    ///   for a forwarded envelope: <c>ConversationId</c> would restart, <c>SagaId</c>
    ///   would drop, and the trace would re-root at the interceptor hop instead of
    ///   continuing the inbound's parent.</item>
    /// </list>
    /// </summary>
    private sealed class RouteBus : MessageBus
    {
        private readonly Envelope _inbound;

        public RouteBus(IWolverineRuntime runtime, Envelope inbound) : base(runtime)
        {
            _inbound = inbound;
            Envelope = inbound;
        }

        internal override void TrackEnvelopeCorrelation(Envelope outbound, Activity? activity)
        {
            // Preserve any per-message Source override (e.g. CloudEvents producer
            // setting a spec-valid `source` URI) before the inherited copy stamps
            // the application service name as a default.
            if (outbound.Source.IsEmpty())
            {
                outbound.Source = Runtime.Options.ServiceName;
            }

            outbound.CopyContextCorrelationFrom(_inbound);
            outbound.Store = Storage;
        }
    }
}
