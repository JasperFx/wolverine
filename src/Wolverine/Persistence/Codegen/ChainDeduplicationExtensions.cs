using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Persistence.Codegen;

/// <summary>
/// GH-4180. Weaves the logical-deduplication frames into a chain that has opted in.
///
/// <para>
/// Deliberately a shared extension rather than three near-identical policies: the frames, their
/// order, and the transactional/non-transactional distinction are identical for message handlers,
/// HTTP endpoints, and gRPC methods. The ONLY per-chain-type variation is what a refusal looks like
/// to the caller, and that is delegated to
/// <see cref="IChain.BuildDeduplicationStopCondition" />.
/// </para>
/// </summary>
public static class ChainDeduplicationExtensions
{
    /// <summary>
    /// Weave the frames without consulting the chain's persistence provider, so the claim is always
    /// written on its own connection and compensated on failure. GH-4505 added the provider-aware
    /// overload; this one remains for a custom chain type that has no codegen container to hand.
    /// </summary>
    [Obsolete(
        "Pass the GenerationRules and IServiceContainer so a store that can write the claim inside its own transaction is able to. GH-4505")]
    public static void ApplyDeduplication(this IChain chain)
        => applyDeduplication(chain, null, null);

    /// <summary>
    /// Insert the deduplication frames at the front of <paramref name="chain" />'s middleware, if it
    /// has opted in. Safe to call more than once — a chain that already carries the frames is left
    /// alone, so a policy and an attribute both asking for deduplication do not double-claim the id
    /// (which would deadlock the second claim against the first on some engines, and on the rest
    /// would simply refuse every message as a duplicate of itself).
    /// </summary>
    public static void ApplyDeduplication(this IChain chain, GenerationRules rules, IServiceContainer container)
        => applyDeduplication(chain, rules, container);

    private static void applyDeduplication(IChain chain, GenerationRules? rules, IServiceContainer? container)
    {
        if (!chain.RequiresDeduplication()) return;
        if (chain.Middleware.OfType<ClaimDeduplicationIdFrame>().Any()) return;
        if (chain.Middleware.Any(x => x is IDeduplicationClaimFrame)) return;

        var requirement = chain.Deduplication!;
        var id = chain.ResolveDeduplicationId(requirement);

        var frames = new List<Frame>();

        if (requirement.Required)
        {
            var missing = new DeduplicationIdMissingFrame(id);
            frames.Add(missing);
            frames.AddRange(
                chain.BuildDeduplicationStopCondition(missing.Variable, DeduplicationOutcome.MissingId, requirement));
        }

        // GH-4505. Ask the provider that owns this chain's transaction whether it can write the claim
        // inside that transaction. When it can, the claim rolls back with the handler's work and there
        // is nothing to compensate for -- which is strictly better than the release path below, and is
        // the story the docs told from the beginning.
        if (rules != null && container != null
                          && rules.GetPersistenceProviders(chain, container)
                              .TryBuildTransactionalDeduplication(chain, id, requirement, container,
                                  out var transactional))
        {
            frames.Add(transactional.Check);
            frames.AddRange(
                chain.BuildDeduplicationStopCondition(transactional.IsDuplicate, DeduplicationOutcome.Duplicate,
                    requirement));
            frames.Add(transactional.Claim);

            if (transactional.CommitRaceWrapper != null)
            {
                frames.Add(transactional.CommitRaceWrapper);
            }

            chain.Middleware.InsertRange(0, frames);
            return;
        }

        var claim = new ClaimDeduplicationIdFrame(id, chain.AncillaryStoreType);
        frames.Add(claim);
        frames.AddRange(
            chain.BuildDeduplicationStopCondition(claim.Variable, DeduplicationOutcome.Duplicate, requirement));

        // GH-4501. The compensating release used to be emitted only when the chain had no ambient
        // transaction, on the reasoning that a transactional chain writes its claim inside the handler's
        // own transaction and a rollback takes the claim with it -- making a release redundant, or worse,
        // a delete of a claim a concurrent caller has since legitimately taken.
        //
        // Nothing implemented that. Every IDeduplicationStore Wolverine ships claims through
        // DbDataSource.CreateCommand(), which opens its own connection: the claim is committed
        // independently of whatever the handler is doing and survives its rollback intact. So the guard
        // skipped the release on exactly the chains that need it -- a [WriteAggregate] endpoint, a
        // [Transactional] handler -- where the first failed attempt permanently poisoned the id and
        // every retry was discarded as a duplicate of work that never happened.
        //
        // GH-4505 makes the original reasoning TRUE for the providers that can honour it, which is the
        // branch above. This one is what remains: a Buffered or Inline endpoint, an HTTP endpoint with no
        // transactional middleware, a store whose message database is somewhere other than the one the
        // handler commits to. Those genuinely have no transaction for the claim to ride, and for them the
        // concurrent-caller hazard the old guard feared cannot arise either -- the row the release deletes
        // is still the one this execution wrote, because it was never rolled back.
        frames.Add(chain.BuildDeduplicationReleaseFrame(id));

        // Front of the queue: the entire point is to refuse before any work happens, including before
        // any other middleware that might have side effects of its own.
        chain.Middleware.InsertRange(0, frames);
    }
}

/// <summary>
/// GH-4505. Marks a provider-supplied claim frame, so <c>ApplyDeduplication</c> stays idempotent on the
/// transactional path the same way <see cref="ClaimDeduplicationIdFrame" /> makes it idempotent on the
/// other one.
/// </summary>
public interface IDeduplicationClaimFrame;
