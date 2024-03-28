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

        Chain.ExecutionLogLevel = LogLevel.Debug;
        Chain.TelemetryEnabled = false;
    }

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        var command = (IAgentCommand)context.Envelope!.Message!;
        
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