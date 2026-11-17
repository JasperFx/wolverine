using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;

namespace Wolverine.Http.Fisher;

/// <summary>
///     Reads the aggregate handler workflow's choices back off a chain.
/// </summary>
/// <remarks>
///     GH-5142: the Marten, Polecat and Fisher copies of this class were identical, and now all forward to
///     one implementation in Wolverine.Http. The class stays so existing calls keep compiling; GH-5145
///     retires the per-store copies.
/// </remarks>
public static class ChainAggregateHandlingExtensions
{
    public static Variable? GetAggregateIdVariable(this IChain chain)
        => AggregateChainLookups.AggregateIdVariable(chain);

    public static bool TryGetAggregateIdVariable(this IChain chain, [MaybeNullWhen(false)] out Variable variable)
        => AggregateChainLookups.TryGetAggregateIdVariable(chain, out variable);

    public static Type? GetAggregateType(this IChain chain)
        => AggregateChainLookups.AggregateType(chain);

    public static bool TryGetAggregateType(this IChain chain, [MaybeNullWhen(false)] out Type type)
        => AggregateChainLookups.TryGetAggregateType(chain, out type);
}
