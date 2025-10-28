using JasperFx.Blocks;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Runtime.Metrics;
using Wolverine.Tracking;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    internal class DirectMetricsPublishingMessageTracker : IMessageTracker
    {
        private readonly WolverineRuntime _runtime;
        private readonly IBlock<IHandlerMetricsData> _sink;
        private readonly string _serviceName;
        private readonly Guid _uniqueNodeId;

        public DirectMetricsPublishingMessageTracker(WolverineRuntime runtime, IBlock<IHandlerMetricsData> sink)
        {
            _runtime = runtime;
            _sink = sink;
            _serviceName = runtime._serviceName;
            _uniqueNodeId = runtime._uniqueNodeId;
            Logger = _runtime.Logger;
        }

        public void LogException(Exception ex, object? correlationId = null, string message = "Exception detected:")
        {
            _runtime.LogException(ex, correlationId, message);
        }
        
        public ILogger Logger { get; }

        public void Sent(Envelope envelope)
        {
            // I think we'll have a different mechanism for this
            _runtime.ActiveSession?.MaybeRecord(MessageEventType.Sent, envelope, _serviceName, _uniqueNodeId);
            _sent(Logger, envelope.CorrelationId!, envelope.GetMessageTypeName(), envelope.Id,
                envelope.Destination?.ToString() ?? string.Empty,
                null);
        }

        public void Received(Envelope envelope)
        {
            _runtime.ActiveSession?.Record(MessageEventType.Received, envelope, _serviceName, _uniqueNodeId);
            _received(Logger, envelope.CorrelationId!, envelope.GetMessageTypeName(), envelope.Id,
                envelope.Destination?.ToString() ?? string.Empty,
                envelope.ReplyUri?.ToString() ?? string.Empty, null);
        }

        public void ExecutionStarted(Envelope envelope)
        {
            _runtime.ExecutionStarted(envelope);
        }

        public void ExecutionFinished(Envelope envelope)
        {
            var executionTime = envelope.StopTiming();
            if (executionTime > 0)
            {
                _sink.Post(new RecordExecutionTime(executionTime, envelope.TenantId));
            }

            _runtime.ActiveSession?.Record(MessageEventType.ExecutionFinished, envelope, _serviceName, _uniqueNodeId);
        }

        public void ExecutionFinished(Envelope envelope, Exception exception)
        {
            ExecutionFinished(envelope);
            _sink.Post(new RecordFailure(exception.GetType().FullNameInCode(), envelope.TenantId));
        }

        public void MessageSucceeded(Envelope envelope)
        {
            var time = DateTimeOffset.UtcNow.Subtract(envelope.SentAt.ToUniversalTime()).TotalMilliseconds;
            _sink.Post(new RecordEffectiveTime(time, envelope.TenantId));
            
            _runtime.ActiveSession?.Record(MessageEventType.MessageSucceeded, envelope, _serviceName, _uniqueNodeId);
        }

        public void MessageFailed(Envelope envelope, Exception ex)
        {
            var time = DateTimeOffset.UtcNow.Subtract(envelope.SentAt.ToUniversalTime()).TotalMilliseconds;
            _sink.Post(new RecordEffectiveTime(time, envelope.TenantId));
            _sink.Post(new RecordDeadLetter(ex.GetType().FullNameInCode(), envelope.TenantId));
            
            _runtime.ActiveSession?.Record(MessageEventType.Sent, envelope, _serviceName, _uniqueNodeId, ex);
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
            _runtime.ActiveSession?.Record(MessageEventType.MovedToErrorQueue, envelope, _serviceName, _uniqueNodeId);
            _movedToErrorQueue(Logger, envelope, ex);
            _sink.Post(new RecordDeadLetter(ex.GetType().FullNameInCode(), envelope.TenantId));
        }

        public void DiscardedEnvelope(Envelope envelope)
        {
            _undeliverable(Logger, envelope, null);
        }

        public void Requeued(Envelope envelope)
        {
            _runtime.Requeued(envelope);
        }
    }
}