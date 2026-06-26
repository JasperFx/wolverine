using System.Diagnostics;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Wolverine.Pulsar.ErrorHandling;

public class PulsarNativeResiliencyContinuation : IContinuation
{
    private readonly Exception _exception;

    public PulsarNativeResiliencyContinuation(Exception exception)
    {
        _exception = exception;
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now, Activity? activity)
    {
        var context = lifecycle as MessageContext;
        var envelope = context?.Envelope;

        if (envelope?.Listener is PulsarListener listener)
        {
            if (listener.NativeRetryLetterQueueEnabled && listener.RetryLetterTopic!.Retry.Count >= envelope.Attempts)
            {
                // Use native retry if enabled and attempts are within bounds
                //await listener.MoveToScheduledUntilAsync(envelope, now);
                await new ScheduledRetryContinuation(listener.RetryLetterTopic!.Retry[envelope.Attempts - 1])
                    .ExecuteAsync(lifecycle, runtime, now, activity);
                return;
            }

            if (listener.NativeDeadLetterQueueEnabled)
            {
                await new MoveToErrorQueue(_exception)
                    .ExecuteAsync(lifecycle, runtime, now, activity);
                //await listener.MoveToErrorsAsync(envelope, _exception);
                return;
            }

            // No native retry-letter or dead-letter topic configured. If the endpoint opted into
            // native per-message redelivery (#3177), leave the message unacknowledged and ask Pulsar
            // to redeliver just it; otherwise keep the historical no-op behavior.
            if (listener.UsesNativeRedelivery)
            {
                await listener.DeferAsync(envelope);
            }

            return;
        }

        // Fall back to MoveToErrorQueue for other listener types
        await new MoveToErrorQueue(_exception).ExecuteAsync(lifecycle, runtime, now, activity);
    }
}