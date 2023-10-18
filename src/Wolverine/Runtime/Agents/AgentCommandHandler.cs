using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

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

        var action = (IAgentCommand)context.Envelope!.Message!;
        try
        {
            await foreach (var cascading in action.ExecuteAsync(_runtime, cancellation))
                await context.EnqueueCascadingAsync(cascading);
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