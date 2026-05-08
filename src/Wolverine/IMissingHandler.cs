using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;

#region sample_imissinghandler
namespace Wolverine;

/// <summary>
///     Hook interface to receive notifications of envelopes received
///     that do not match any known handlers within the system
/// </summary>
public interface IMissingHandler
{
    /// <summary>
    ///     Executes for unhandled envelopes
    /// </summary>
    /// <param name="context"></param>
    /// <param name="root"></param>
    /// <returns></returns>
    ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root);
}

#endregion

internal class MoveUnknownMessageToDeadLetterQueue : IMissingHandler
{
    public async ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root)
    {
        var envelope = context.Envelope!;
        await context.MoveToDeadLetterQueueAsync(
            new UnknownMessageTypeNameException($"Unknown message type: '{envelope.MessageType}'"));

        // Unknown-message-type DLQ moves bypass auto-Fault publishing — there is
        // no T to construct Fault<T> for. Emit a one-line trace so operators can
        // correlate missing fault events back to unknown-type failures.
        if (root.Options.FaultPublishing.GlobalMode != FaultPublishingMode.None)
        {
            root.Logger.LogDebug(
                "Unknown-message-type DLQ for envelope {EnvelopeId} (type-name '{MessageType}') bypassed auto-Fault publishing",
                envelope.Id, envelope.MessageType);
            Activity.Current?.AddEvent(new ActivityEvent(
                WolverineTracing.FaultBypassedUnknownType,
                tags: new ActivityTagsCollection
                {
                    [WolverineTracing.MessageType] = envelope.MessageType
                }));
        }
    }
}
