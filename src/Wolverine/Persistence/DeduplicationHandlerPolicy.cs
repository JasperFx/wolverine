using JasperFx;
using JasperFx.Core;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Logging;
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
        if (_options.Durability.EnableMessageDeduplication) return;

        WarnDeduplicationIsNotEnabled(container, opted.Select(x => x.Description));
    }

    /// <summary>
    /// Warn — loudly, and once per host — when a chain asked for logical deduplication but the storage
    /// that backs it was never turned on.
    ///
    /// <para>
    /// This deliberately warns rather than throws, and the reason is worth stating because "fail fast"
    /// is the obvious instinct here and it is the wrong one. <c>[Deduplicated]</c> lives on a handler
    /// TYPE, and handler types get discovered by every host that scans the assembly they live in.
    /// A modular monolith where one module enables deduplication and a sibling host does not is an
    /// entirely legitimate setup, and a hard failure makes the attribute unusable in any shared
    /// assembly — the sibling host cannot start, through no fault of its own configuration.
    /// </para>
    ///
    /// <para>
    /// The property that actually matters — an application is never <i>silently</i> unprotected — is
    /// not weakened by warning here, because it is not enforced here. It is enforced at the point of
    /// use: <see cref="MessageDeduplicator.TryClaimAsync" /> throws on a store whose
    /// <see cref="IDeduplicationStore.Enabled" /> is false rather than answering "yes, that's new" to
    /// every id. A misconfigured host still cannot process a single duplicate quietly; it just gets to
    /// start, and to tell its operator why at startup instead of at the first message.
    /// </para>
    /// </summary>
    private static void WarnDeduplicationIsNotEnabled(IServiceContainer container, IEnumerable<string> descriptions)
    {
        var logger = container.GetInstance<ILoggerFactory>().CreateLogger<DeduplicationHandlerPolicy>();

        logger.LogWarning(
            "Logical message deduplication was requested by {Chains}, but {Setting} is false, so there is no storage to enforce it. These handlers will throw on their first message. Set opts.Durability.{Setting} = true (this provisions a new table) or remove the [Deduplicated] usage. See GH-4180",
            descriptions.Join(", "), nameof(DurabilitySettings.EnableMessageDeduplication),
            nameof(DurabilitySettings.EnableMessageDeduplication));
    }
}
