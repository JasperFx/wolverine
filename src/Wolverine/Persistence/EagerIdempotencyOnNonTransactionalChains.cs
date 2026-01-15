using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

internal class EagerIdempotencyOnNonTransactionalChains : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var handlerChain in chains.Where(x => !x.IsTransactional))
        {
            handlerChain.Middleware.Insert(0, MethodCall.For<MessageContext>(x => x.AssertEagerIdempotencyAsync(CancellationToken.None)));
            
            handlerChain.Postprocessors.Add(MethodCall.For<MessageContext>(x => x.PersistHandledAsync()));
        }
    }
}