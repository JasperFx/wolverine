using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Http;

/// <summary>
///     GH-5142. The one implementation behind the <c>ChainAggregateHandlingExtensions</c> that
///     Wolverine.Http.Marten, Wolverine.Http.Polecat and Wolverine.Http.Fisher each publish. The three copies
///     were byte-for-byte the same reads of the chain's <c>AggregateHandling</c> tag, which has lived in core
///     since GH-3907.
/// </summary>
/// <remarks>
///     Deliberately not an extension class. Every store's extensions are in that store's namespace, and an
///     application nearly always imports <c>Wolverine.Http</c> beside it, so a second extension method with the
///     same signature here would make every such call ambiguous (CS0121). The per-store classes forward to
///     this one; retiring them is GH-5145.
/// </remarks>
internal static class AggregateChainLookups
{
    /// <summary>
    ///     The variable holding the aggregate's identity on a chain that uses the aggregate handler workflow
    ///     for exactly one aggregate, or null otherwise.
    /// </summary>
    public static Variable? AggregateIdVariable(IChain chain)
        => AggregateHandling.TryLoad(chain, out var handling) ? handling.AggregateId : null;

    public static bool TryGetAggregateIdVariable(IChain chain, [MaybeNullWhen(false)] out Variable variable)
    {
        variable = AggregateIdVariable(chain);
        return variable != null;
    }

    /// <summary>
    ///     The aggregate type on a chain that uses the aggregate handler workflow for exactly one aggregate,
    ///     or null otherwise.
    /// </summary>
    public static Type? AggregateType(IChain chain)
        => AggregateHandling.TryLoad(chain, out var handling) ? handling.AggregateType : null;

    public static bool TryGetAggregateType(IChain chain, [MaybeNullWhen(false)] out Type type)
    {
        type = AggregateType(chain);
        return type != null;
    }
}
