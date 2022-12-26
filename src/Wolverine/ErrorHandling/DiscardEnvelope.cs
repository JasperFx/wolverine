using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

public class DiscardEnvelope : IContinuation, IContinuationSource
{
    public static readonly DiscardEnvelope Instance = new();

    private DiscardEnvelope()
    {
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now)
    {
        try
        {
            runtime.MessageLogger.DiscardedEnvelope(lifecycle.Envelope!);
            await lifecycle.CompleteAsync();
        }
        catch (Exception e)
        {
            runtime.Logger.LogError(e, "Failure while attempting to discard an envelope");
        }
    }

    public string Description => "Discard the message";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }
}