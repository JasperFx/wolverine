using JasperFx;
using JasperFx.Core;
using JasperFx.CodeGeneration;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Codegen;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

/// <summary>
/// GH-4180. Weaves the logical deduplication frames into every message handler chain that opted in,
/// through <c>[Deduplicated]</c> or <c>Policies.RequireDeduplicationId()</c>.
///
/// <para>
/// Runs as a policy rather than from the attribute itself because the frames it emits depend on
/// <see cref="IChain.IsTransactional" />, which the persistence providers set from their own
/// policies. Reading it any earlier would see <see langword="false" /> on a chain that is about to
/// become transactional and emit a compensating release that the transaction makes wrong. This is
/// the same phase, and for the same reason, that
/// <see cref="EagerIdempotencyOnNonTransactionalChains" /> reads it in.
/// </para>
/// </summary>
internal class DeduplicationHandlerPolicy : IHandlerPolicy
{
    private readonly WolverineOptions _options;

    public DeduplicationHandlerPolicy(WolverineOptions options)
    {
        _options = options;
    }

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        // Validation only -- the frames themselves are woven later, from HandlerChain.applyCustomizations,
        // which is the first point where both [Deduplicated] and IsTransactional are settled.
        //
        // HasAttribute is checked alongside the already-set requirement because [Deduplicated] is a
        // ModifyChainAttribute and has NOT been applied yet at this point in the bootstrap; only the
        // requirements set by RequireDeduplicationIdPolicy are visible on the chain so far.
        var opted = chains
            .Where(x => x.RequiresDeduplication() || x.HasAttribute<DeduplicatedAttribute>())
            .ToArray();

        if (opted.Length == 0) return;

        AssertDeduplicationIsEnabled(_options, opted.Select(x => x.Description));
    }

    /// <summary>
    /// Fail at bootstrap rather than at runtime when a chain asked for logical deduplication but the
    /// storage that backs it was never turned on.
    ///
    /// <para>
    /// The alternative — <see cref="NullDeduplicationStore" /> quietly answering "yes, that's new" for
    /// every id — is the worst available outcome: the application reports itself as protected, every
    /// duplicate runs, and there is no log line, no metric, and no failing test that distinguishes it
    /// from a working configuration. A misconfiguration this quiet is only ever found in production.
    /// </para>
    /// </summary>
    internal static void AssertDeduplicationIsEnabled(WolverineOptions options, IEnumerable<string> descriptions)
    {
        if (options.Durability.EnableMessageDeduplication) return;

        throw new InvalidOperationException(
            $"Logical message deduplication was requested by {descriptions.Join(", ")}, but {nameof(DurabilitySettings)}.{nameof(DurabilitySettings.EnableMessageDeduplication)} is false, so there is no storage to enforce it. Set opts.Durability.{nameof(DurabilitySettings.EnableMessageDeduplication)} = true (this provisions a new table) or remove the [Deduplicated] usage. See GH-4180");
    }
}
