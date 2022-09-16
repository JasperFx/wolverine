using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

public class NoHandlerContinuation : IContinuation
{
    private readonly IReadOnlyList<IMissingHandler> _handlers;
    private readonly IWolverineRuntime _root;

    public NoHandlerContinuation(IReadOnlyList<IMissingHandler> handlers, IWolverineRuntime root)
    {
        _handlers = handlers;
        _root = root;
    }

    public async ValueTask ExecuteAsync(IMessageContext context,
        IWolverineRuntime runtime,
        DateTimeOffset now)
    {
        if (context.Envelope == null) throw new InvalidOperationException("Context does not have an Envelope");

        runtime.MessageLogger.NoHandlerFor(context.Envelope!);

        foreach (var handler in _handlers)
        {
            try
            {
                await handler.HandleAsync(context, _root);
            }
            catch (Exception? e)
            {
                runtime.Logger.LogError(e, "Failure in 'missing handler' execution");
            }
        }

        if (context.Envelope.AckRequested)
        {
            await context.SendAcknowledgementAsync();
        }

        await context.CompleteAsync();

        // These two lines are important to make the message tracking work
        // if there is no handler
        runtime.MessageLogger.ExecutionFinished(context.Envelope);
        runtime.MessageLogger.MessageSucceeded(context.Envelope);
    }
}
