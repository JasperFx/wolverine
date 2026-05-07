using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal sealed class DiscardEnvelopeSource : IContinuationSource
{
    public static readonly DiscardEnvelopeSource Instance = new();

    private DiscardEnvelopeSource()
    {
    }

    public string Description => "Discard the message";

    public IContinuation Build(Exception ex, Envelope envelope)
        => new DiscardEnvelope(ex);
}

public sealed class DiscardEnvelope : IContinuation
{
    public DiscardEnvelope(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public Exception Exception { get; }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        try
        {
            activity?.AddEvent(new ActivityEvent(WolverineTracing.EnvelopeDiscarded));

            // Publish-fault BEFORE CompleteAsync and BEFORE the DiscardedEnvelope
            // tracking event: the original envelope's terminal sweep would otherwise
            // close the tracking session before the fault publish lands.
            await runtime.PublishFaultIfEnabledAsync(lifecycle,
                Exception,
                FaultTrigger.Discarded,
                activity);

            await lifecycle.CompleteAsync();

            runtime.MessageTracking.DiscardedEnvelope(lifecycle.Envelope!);
        }
        catch (Exception e)
        {
            runtime.Logger.LogError(e, "Failure while attempting to discard an envelope");
        }
    }
}
