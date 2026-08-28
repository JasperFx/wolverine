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

        // The compensating release is ONLY for chains with no ambient transaction. Where one exists the
        // claim is written inside it, so a rollback already removes it -- and releasing on top of that
        // would delete a claim that either no longer exists or, worse, has since been legitimately taken
        // by a concurrent caller.
        //
        // Read here rather than at attribute time on purpose: IsTransactional is set by the persistence
        // providers' own policies, so it is only trustworthy once those have run. This is the same phase
        // EagerIdempotencyOnNonTransactionalChains reads it in.
        if (!chain.IsTransactional)
        {
            frames.Add(new ReleaseDeduplicationIdOnFailureFrame(id, chain.AncillaryStoreType));
        }

        // Front of the queue: the entire point is to refuse before any work happens, including before
        // any other middleware that might have side effects of its own.
        chain.Middleware.InsertRange(0, frames);
    }
}
