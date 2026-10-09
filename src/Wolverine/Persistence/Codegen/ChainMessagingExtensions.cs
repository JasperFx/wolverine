using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using JasperFx;

namespace Wolverine.Persistence.Codegen;

/// <summary>
///     GH-4907. Whether a chain can send messages at all: through an injected <see cref="IMessageBus" /> or
///     <see cref="IMessageContext" />, by returning <see cref="OutgoingMessages" />, or by cascading a return
///     value. A persistence provider uses this to decide that a chain which only READS through its store but
///     sends messages still needs the commit -- the envelopes are queued on the outbox-enrolled session, and
///     without a commit they are never persisted.
/// </summary>
public static class ChainMessagingExtensions
{
    public static bool CanSendMessages(this IChain chain, IServiceContainer container)
    {
        if (chain.ServiceDependencies(container, [typeof(IMessageBus), typeof(IMessageContext)])
            .Any(x => x == typeof(IMessageBus) || x == typeof(IMessageContext)))
        {
            return true;
        }

        if (chain.ReturnVariablesOfType<OutgoingMessages>().Any())
        {
            return true;
        }

        // A message handler cascades whatever its handler methods return. An HTTP endpoint writes its first
        // return value as the response, so only the ones after it cascade.
        var firstCascadingReturn = chain is HandlerChain ? 0 : 1;
        return chain.HandlerCalls().Any(call => call.Creates.Skip(firstCascadingReturn).Any());
    }
}
