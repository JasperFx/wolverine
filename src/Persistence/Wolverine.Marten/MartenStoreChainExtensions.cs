using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;

namespace Wolverine.Marten;

public static class MartenStoreChainExtensions
{
    /// <summary>
    /// Route a chain to a Marten ancillary (secondary) document store. Sets
    /// <see cref="IChain.AncillaryStoreType"/> and inserts the Marten outbox-factory frame at the front
    /// of the chain's middleware so the handler opens and commits through that store's
    /// outbox-enrolled session. Callable directly from an <see cref="IChainPolicy"/> so a whole
    /// assembly of handlers can be routed to one ancillary store without per-handler attributes.
    /// </summary>
    /// <param name="chain">The handler or HTTP chain</param>
    /// <param name="storeType">The Marten store marker type (e.g. <c>IMyStore : IDocumentStore</c>)</param>
    public static void UseMartenStore(this IChain chain, Type storeType)
    {
        chain.AncillaryStoreType = storeType;
        chain.Middleware.Insert(0, new AncillaryOutboxFactoryFrame(storeType));
    }

    /// <summary>
    /// GH-5721. <see cref="IChain.UseForResponse" />, except the response is produced before the chain commits its
    /// Marten session instead of after it. For a custom <see cref="IResponseAware" /> whose response reads this
    /// transaction's work, so a <c>[DeduplicatedWithResponse]</c> endpoint can store it with a claim written in the
    /// same transaction. Nothing checks what the response reads: under <c>EventAppendMode.Quick</c>, say, it is up to
    /// the response to do what <c>UpdatedAggregate</c> does with <c>ProjectLatestForCommit</c>.
    /// </summary>
    public static void UseForResponseBeforeCommit(this IChain chain, MethodCall frame)
    {
        var existing = new HashSet<Frame>(chain.Postprocessors, ReferenceEqualityComparer.Instance);
        chain.UseForResponse(frame);

        // A commit added later by a policy, as for a returned IMartenOp, is appended after the response anyway
        if (!chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any()) return;

        var added = chain.Postprocessors.Where(x => !existing.Contains(x)).ToArray();
        chain.Postprocessors.RemoveAll(x => !existing.Contains(x));

        var commit = chain.Postprocessors.FindIndex(x => x is DocumentSessionSaveChanges);
        chain.Postprocessors.InsertRange(commit, added);
    }
}
