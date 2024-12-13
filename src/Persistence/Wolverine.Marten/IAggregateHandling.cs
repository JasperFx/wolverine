using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;

namespace Wolverine.Marten;

internal record AggregateHandling(Type AggregateType, Variable AggregateId)
{
    public void Store(IChain chain)
    {
        chain.Tags[nameof(AggregateHandling)] = this;
    }

    public static bool TryLoad(IChain chain, out AggregateHandling handling)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            if (raw is AggregateHandling h)
            {
                handling = h;
                return true;
            }
        }

        handling = default;
        return false;
    }
}
