using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Wolverine.ComplianceTests;

public static class CompileHandlersExtensions
{
    /// <summary>
    ///     GH-5676. Compile the handler chains of <typeparamref name="THandler" /> now, as part of setting the host up,
    ///     rather than on the first message. A host in Dynamic code generation compiles a chain with Roslyn the first
    ///     time a message reaches it, and the first compile in a test process is slow (several seconds on a busy CI
    ///     runner). Without this, the first test a process runs pays for that inside its tracked session's timeout,
    ///     and fails on a cold process only
    /// </summary>
    public static void CompileHandlersFor<THandler>(this IHost host)
    {
        var handlers = host.GetRuntime().Handlers;
        var messageTypes = handlers.Chains
            .Where(chain => chain.Handlers.Any(call => call.HandlerType == typeof(THandler)))
            .Select(chain => chain.MessageType)
            .Distinct();

        foreach (var messageType in messageTypes)
        {
            // A message type with several handler types has no single handler of its own (GH-5136); each chain
            // is compiled for the endpoint it is bound to instead
            if (handlers.ChainsFor(messageType).Count == 1)
            {
                handlers.HandlerFor(messageType);
            }
        }
    }
}
