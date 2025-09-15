using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

/// <summary>
/// If a handler method or handler type is decorated with this attribute, Wolverine will track
/// any cascaded messages using its SagaId so that eventual responses back to the SignalR transport
/// will to the current connection
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class EnlistInCurrentConnectionSagaAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.Middleware.Insert(0, new MethodCall(typeof(EnlistmentOperations), nameof(EnlistmentOperations.EnlistInConnectionSaga)));
    }
}

