using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Tracking;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    /// <summary>
    ///     The single metrics-silent tracker instance handed to every construction site that carries
    ///     system traffic. See <see cref="SystemTrafficMessageTracker" />.
    /// </summary>
    internal IMessageTracker SystemTraffic => _systemTraffic ??= new SystemTrafficMessageTracker(this);

    private SystemTrafficMessageTracker? _systemTraffic;

    /// <summary>
    ///     The message tracker a sending agent, local queue, or listening pipeline for this endpoint
    ///     should use — resolved ONCE at construction, never per envelope. System endpoints (node control
    ///     queues, reply queues, the internal local queues) and endpoints whose telemetry is deliberately
    ///     switched off get the metrics-silent tracker, so their traffic never reaches the meters or the
    ///     CritterWatch accumulator. CritterWatch GH-907: a monitoring tool must not measurably inflate
    ///     the thing it is monitoring, and an idle app was reporting hundreds of messages a minute of
    ///     nothing but its own control-queue and monitoring traffic.
    /// </summary>
    internal IMessageTracker MessageTrackingFor(Endpoint endpoint)
    {
        return endpoint.Role == EndpointRole.System || !endpoint.TelemetryEnabled
            ? SystemTraffic
            : MessageTracking;
    }

    /// <summary>
    ///     CritterWatch GH-907. An <see cref="IMessageTracker" /> for Wolverine's own internal traffic —
    ///     agent commands on the node control queues, acknowledgements, CritterWatch monitoring messages —
    ///     that keeps the debug logging, tracked-session bookkeeping, and wire taps of the standard
    ///     tracker while emitting <b>no metrics</b>: no meter counters or histograms, and nothing into the
    ///     CritterWatch accumulator (which would otherwise publish the system's own noise back to the
    ///     console as apparent application volume — a feedback loop, since those publishes are themselves
    ///     messages that get counted).
    ///
    ///     <para>Deliberately a separate tracker resolved once at construction time (per endpoint, per
    ///     executor) rather than an if-test inside the counting methods: Wolverine keeps per-envelope
    ///     branching out of the hot path, the same way <c>InlineSendingAgent</c> picks
    ///     <c>sendWithTracing</c> vs <c>sendWithOutTracing</c> up front.</para>
    /// </summary>
    internal class SystemTrafficMessageTracker : IMessageTracker
    {
        private readonly WolverineRuntime _runtime;

        public SystemTrafficMessageTracker(WolverineRuntime runtime)
        {
            _runtime = runtime;
        }

        public void Sent(Envelope envelope)
        {
            _runtime.ActiveSession?.MaybeRecord(MessageEventType.Sent, envelope, _runtime._serviceName,
                _runtime._uniqueNodeId);
            _sent(_runtime.Logger, envelope.CorrelationId!, envelope.GetMessageTypeName(), envelope.Id,
                envelope.Destination?.ToString() ?? string.Empty, null);

            _runtime.fireWireTapSuccess(envelope);
        }

        public void Received(Envelope envelope)
        {
            _runtime.ActiveSession?.Record(MessageEventType.Received, envelope, _runtime._serviceName,
                _runtime._uniqueNodeId);
            _received(_runtime.Logger, envelope.CorrelationId!, envelope.GetMessageTypeName(), envelope.Id,
                envelope.Destination?.ToString() ?? string.Empty,
                envelope.ReplyUri?.ToString() ?? string.Empty, null);
        }

        public void ExecutionStarted(Envelope envelope)
        {
            _runtime.ExecutionStarted(envelope);
        }

        public void ExecutionFinished(Envelope envelope)
        {
            // Still stop the timing so the envelope's state stays symmetric with ExecutionStarted;
            // the measurement is simply not recorded anywhere.
            envelope.StopTiming();
            _runtime.ActiveSession?.Record(MessageEventType.ExecutionFinished, envelope, _runtime._serviceName,
                _runtime._uniqueNodeId);
        }

        public void ExecutionFinished(Envelope envelope, Exception exception)
        {
            ExecutionFinished(envelope);
        }

        public void MessageSucceeded(Envelope envelope)
        {
            _runtime.ActiveSession?.Record(MessageEventType.MessageSucceeded, envelope, _runtime._serviceName,
                _runtime._uniqueNodeId);

            _runtime.fireWireTapSuccess(envelope);
        }

        public void MessageFailed(Envelope envelope, Exception ex)
        {
            // The base tracker records this with MessageEventType.Sent as well — see
            // WolverineRuntime.MessageFailed
            _runtime.ActiveSession?.Record(MessageEventType.Sent, envelope, _runtime._serviceName,
                _runtime._uniqueNodeId, ex);

            _runtime.fireWireTapFailure(envelope, ex);
        }

        public void NoHandlerFor(Envelope envelope)
        {
            _runtime.NoHandlerFor(envelope);
        }

        public void NoRoutesFor(Envelope envelope)
        {
            _runtime.NoRoutesFor(envelope);
        }

        public void MovedToErrorQueue(Envelope envelope, Exception ex)
        {
            _runtime.ActiveSession?.Record(MessageEventType.MovedToErrorQueue, envelope, _runtime._serviceName,
                _runtime._uniqueNodeId);
            _movedToErrorQueue(_runtime.Logger, envelope, ex);
        }

        public void DiscardedEnvelope(Envelope envelope)
        {
            _runtime.DiscardedEnvelope(envelope);
        }

        public void Requeued(Envelope envelope)
        {
            _runtime.Requeued(envelope);
        }

        public void LogException(Exception ex, object? correlationId = null,
            string message = "Exception detected:")
        {
            _runtime.LogException(ex, correlationId, message);
        }

        public void LogStatus(string message)
        {
            _runtime.ActiveSession?.LogStatus(message);
        }
    }
}
