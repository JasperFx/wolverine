using JasperFx.Blocks;
using JasperFx.Core.Reflection;
using Wolverine.Logging;
using Wolverine.Runtime.Metrics;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    internal class HybridMetricsPublishingMessageTracker : IMessageTracker
    {
        private readonly WolverineRuntime _runtime;
        private readonly IBlock<IHandlerMetricsData> _sink;

        public HybridMetricsPublishingMessageTracker(WolverineRuntime runtime, IBlock<IHandlerMetricsData> sink)
        {
            _runtime = runtime;
            _sink = sink;
        }
        
        public void LogException(Exception ex, object? correlationId = null, string message = "Exception detected:")
        {
            _runtime.LogException(ex, correlationId, message);
        }

        public void Sent(Envelope envelope)
        {
            _runtime.Sent(envelope);
        }

        public void Received(Envelope envelope)
        {
            _runtime.Received(envelope);
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

            _runtime.ExecutionFinished(envelope);
        }

        public void ExecutionFinished(Envelope envelope, Exception exception)
        {
            _runtime.ExecutionFinished(envelope, exception);
            _sink.Post(new RecordFailure(exception.GetType().FullNameInCode(), envelope.TenantId));
        }

        public void MessageSucceeded(Envelope envelope)
        {
            var time = DateTimeOffset.UtcNow.Subtract(envelope.SentAt.ToUniversalTime()).TotalMilliseconds;
            _sink.Post(new RecordEffectiveTime(time, envelope.TenantId));
            
            _runtime.MessageSucceeded(envelope);
        }

        public void MessageFailed(Envelope envelope, Exception ex)
        {
            var time = DateTimeOffset.UtcNow.Subtract(envelope.SentAt.ToUniversalTime()).TotalMilliseconds;
            _sink.Post(new RecordEffectiveTime(time, envelope.TenantId));
            _sink.Post(new RecordDeadLetter(ex.GetType().FullNameInCode(), envelope.TenantId));
            
            _runtime.MessageFailed(envelope, ex);
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
            _runtime.MovedToErrorQueue(envelope, ex);
            _sink.Post(new RecordDeadLetter(ex.GetType().FullNameInCode(), envelope.TenantId));
        }

        public void DiscardedEnvelope(Envelope envelope)
        {
            _runtime.DiscardedEnvelope(envelope);
        }

        public void Requeued(Envelope envelope)
        {
            _runtime.Requeued(envelope);
        }
    }
}