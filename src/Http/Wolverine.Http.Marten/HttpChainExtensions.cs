using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Model;
using Wolverine.Marten;

namespace Wolverine.Http.Marten;

public static class HttpChainExtensions
{
    public static Variable? GetAggregateIdVariable(this HttpChain chain)
        => chain.Tags.TryGetValue(nameof(AggregateHandling), out var obj) && obj is AggregateHandling aggregateHandling
            ? aggregateHandling.AggregateId
            : null;

    public static bool TryGetAggregateIdVariable(this HttpChain chain, [MaybeNullWhen(false)] out Variable variable)
    {
        variable = chain.GetAggregateIdVariable();
        return variable != null;
    }
}