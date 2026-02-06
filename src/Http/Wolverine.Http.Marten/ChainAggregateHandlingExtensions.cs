using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Marten;

namespace Wolverine.Http.Marten;

public static class ChainAggregateHandlingExtensions
{
    public static Variable? GetAggregateIdVariable(this IChain chain)
        => chain.Tags.TryGetValue(nameof(AggregateHandling), out var obj) && obj is AggregateHandling aggregateHandling
            ? aggregateHandling.AggregateId
            : null;

    public static bool TryGetAggregateIdVariable(this IChain chain, [MaybeNullWhen(false)] out Variable variable)
    {
        variable = chain.GetAggregateIdVariable();
        return variable != null;
    }

    public static Type? GetAggregateType(this IChain chain)
        => chain.Tags.TryGetValue(nameof(AggregateHandling), out var obj) && obj is AggregateHandling aggregateHandling
            ? aggregateHandling.AggregateType
            : null;
    
    public static bool TryGetAggregateType(this IChain chain, [MaybeNullWhen(false)] out Type type)
    {
        type = chain.GetAggregateType();
        return type != null;
    }
}