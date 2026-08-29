using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

/// <summary>
/// GH-4180. Applies a <see cref="DeduplicationRequirement" /> to every handler chain matching a
/// filter, for applications that would rather express "all create-style commands are deduplicated"
/// once than decorate each handler with <c>[Deduplicated]</c>.
///
/// <para>
/// Sets the requirement only. <see cref="DeduplicationHandlerPolicy" /> does the weaving afterwards,
/// so a chain reached by both this policy and the attribute is configured once and woven once.
/// A chain that already carries an explicit requirement is left alone — the attribute on the
/// handler is the more specific statement and wins over the blanket rule.
/// </para>
/// </summary>
internal class RequireDeduplicationIdPolicy : IHandlerPolicy
{
    private readonly Func<HandlerChain, bool> _filter;
    private readonly DeduplicationRequirement _requirement;

    public RequireDeduplicationIdPolicy(Func<HandlerChain, bool> filter, DeduplicationRequirement requirement)
    {
        _filter = filter;
        _requirement = requirement;
    }

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(x => x.Deduplication == null && _filter(x)))
        {
            chain.Deduplication = _requirement;
        }
    }
}
