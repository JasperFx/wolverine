using Microsoft.Extensions.ObjectPool;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : PooledObjectPolicy<MessageContext>
{
    public override bool Return(MessageContext context)
    {
        context.ClearState();
        return true;
    }

    public override MessageContext Create()
    {
        return new MessageContext(this);
    }
}