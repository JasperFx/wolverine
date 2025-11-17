using Wolverine.Runtime;
using Wolverine.Runtime.Interop;

#region sample_IMissingHandler
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
        await context.MoveToDeadLetterQueueAsync(new UnknownMessageTypeNameException($"Unknown message type: '{context.Envelope!.MessageType}'"));
    }
}