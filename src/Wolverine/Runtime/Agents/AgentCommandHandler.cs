using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.Util;

namespace Wolverine.Runtime.Agents;

internal class AgentCommandHandler : MessageHandler
{
    private readonly WolverineRuntime _runtime;

    public AgentCommandHandler(WolverineRuntime runtime)
    {
        _runtime = runtime;
        Chain = new HandlerChain(typeof(IAgentCommand), runtime.Handlers);
        Chain.OnException<AgentCommandException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds())
            .Then.Discard();

        // Reducing log noise
        Chain.ProcessingLogLevel = LogLevel.None;
        Chain.SuccessLogLevel = LogLevel.None;
        Chain.TelemetryEnabled = false;
    }

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        var command = (IAgentCommand)context.Envelope!.Message!;

        // GH-3748: a remotely-received batch of agent starts or stops executes off the control lane.
        // The database control endpoint is strictly serial, so running a slow batch inline here starved
        // every other control message to this node — stops queued behind it, and the leader had no way
        // to learn the batch was even progressing. The runner sends the correlated typed reply itself
        // when the work finishes; this handler is done the moment the work is accepted.
        //
        // Local inline invocations keep executing synchronously below — their caller reads the reply
        // from the in-context cascade, and they already run outside the control lane. They are detected
        // by ResponseType, which only the in-process Executor.InvokeAsync<T> path sets; ReplyUri alone
        // cannot tell the two apart because the local path stamps the loopback replies URI too.
        if (command is IDeferredAgentWork
            && context.Envelope.ResponseType == null
            && context.Envelope.ReplyUri != null
            && context.Envelope.ReplyRequested.IsNotEmpty()
            && _runtime.NodeController?.DeferredWork is { } deferredWork)
        {
            var conversationId = context.Envelope.ConversationId == Guid.Empty
                ? context.Envelope.Id
                : context.Envelope.ConversationId;

            if (deferredWork.TryEnqueue(command, context.Envelope.ReplyRequested!, context.Envelope.ReplyUri,
                    conversationId))
            {
                // The executor would otherwise see a reply-requested envelope whose handler produced no
                // response and IMMEDIATELY send a "no response was created" FailureAcknowledgement —
                // faulting the requester's InvokeAsync<T> the instant the work was accepted. The reply
                // obligation now belongs to the deferred runner.
                context.Envelope.ReplyRequested = null;
                return;
            }

            // The runner is shutting down; fall through to the inline path rather than dropping the
            // command on the floor.
        }

        try
        {
            var results = await command.ExecuteAsync(_runtime, cancellation);
            if (context.Envelope.ReplyRequested.IsNotEmpty() &&
                context.Envelope.ReplyRequested != typeof(AgentCommands).ToMessageTypeName())
            {
                foreach (var agentCommand in results)
                {
                    await context.EnqueueCascadingAsync(agentCommand);
                }
            }
            else
            {
                await context.EnqueueCascadingAsync(results);
            }

        }
        catch (TimeoutException)
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            throw;
        }
    }
}