using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;

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
    /// Insert the deduplication frames at the front of <paramref name="chain" />'s middleware, if it
    /// has opted in. Safe to call more than once — a chain that already carries the frames is left
    /// alone, so a policy and an attribute both asking for deduplication do not double-claim the id
    /// (which would deadlock the second claim against the first on some engines, and on the rest
    /// would simply refuse every message as a duplicate of itself).
    /// </summary>
    public static void ApplyDeduplication(this IChain chain)
    {
        if (!chain.RequiresDeduplication()) return;
        if (chain.Middleware.OfType<ClaimDeduplicationIdFrame>().Any()) return;

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

        var claim = new ClaimDeduplicationIdFrame(id, chain.AncillaryStoreType);
        frames.Add(claim);
        frames.AddRange(
            chain.BuildDeduplicationStopCondition(claim.Variable, DeduplicationOutcome.Duplicate, requirement));

        // GH-4501. The compensating release used to be emitted only when the chain had no ambient
        // transaction, on the reasoning that a transactional chain writes its claim inside the handler's
        // own transaction and a rollback takes the claim with it -- making a release redundant, or worse,
        // a delete of a claim a concurrent caller has since legitimately taken.
        //
        // Nothing implements that. Every IDeduplicationStore Wolverine ships claims through
        // DbDataSource.CreateCommand(), which opens its own connection: the claim is committed
        // independently of whatever the handler is doing and survives its rollback intact. So the guard
        // skipped the release on exactly the chains that need it -- a [WriteAggregate] endpoint, a
        // [Transactional] handler -- where the first failed attempt permanently poisoned the id and
        // every retry was discarded as a duplicate of work that never happened.
        //
        // The concurrent-caller hazard the guard was protecting against cannot arise either, for the
        // same reason: the row the release deletes is still the one this execution wrote, because it was
        // never rolled back and so no one else could claim the id in the meantime.
        frames.Add(chain.BuildDeduplicationReleaseFrame(id));

        // Front of the queue: the entire point is to refuse before any work happens, including before
        // any other middleware that might have side effects of its own.
        chain.Middleware.InsertRange(0, frames);
    }
}
