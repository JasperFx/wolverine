using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal class NoHandlerContinuation : IContinuation
{
    private readonly IReadOnlyList<IMissingHandler> _handlers;
    private readonly IWolverineRuntime _root;

    public NoHandlerContinuation(IReadOnlyList<IMissingHandler> handlers, IWolverineRuntime root)
    {
        _handlers = handlers;
        _root = root;
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        if (lifecycle.Envelope == null)
        {
            throw new InvalidOperationException("Context does not have an Envelope");
        }

        activity?.AddEvent(new ActivityEvent(WolverineTracing.NoHandler));

        runtime.MessageTracking.NoHandlerFor(lifecycle.Envelope!);

        foreach (var handler in _handlers)
        {
            try
            {
                await handler.HandleAsync(lifecycle, _root);
            }
            catch (Exception? e)
            {
                runtime.Logger.LogError(e, "Failure in 'missing handler' execution");
            }
        }

        if (lifecycle.Envelope.AckRequested || lifecycle.Envelope.ReplyRequested.IsNotEmpty())
        {
            await lifecycle.SendFailureAcknowledgementAsync(
                $"No known message handler for message type '{lifecycle.Envelope.MessageType}'");
        }

        await lifecycle.CompleteAsync();

        // These two lines are important to make the message tracking work
        // if there is no handler. Routed through the executor-resolved tracker so an
        // unhandled SYSTEM message doesn't record success metrics (GH-907 / #3774).
        var tracker = lifecycle.CompletionTrackerFor(runtime);
        tracker.ExecutionFinished(lifecycle.Envelope);
        tracker.MessageSucceeded(lifecycle.Envelope);
    }
}