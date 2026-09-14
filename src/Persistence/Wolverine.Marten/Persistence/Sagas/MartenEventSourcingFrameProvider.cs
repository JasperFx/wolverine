using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Marten.Persistence.Sagas;

// GH-3907: the Marten half of the shared aggregate handler workflow's store seam. Deliberately a
// sibling of IPersistenceFrameProvider's contract rather than more members on it, so stores with no
// event sourcing never grow no-op aggregate members - but implemented on the *same class*, which is
// what lets Wolverine find it through the persistence strategies already registered on
// GenerationRules instead of a second registry that would have to know Marten exists.
internal partial class MartenPersistenceFrameProvider : IEventSourcingFrameProvider
{
    public string StoreName => "Marten";

    // Wolverine.Marten.Events and UnknownAggregateException stay public and store-side - GH-3907
    // retires nothing. The workflow only needs to recognize them, so they come over the seam rather
    // than core naming either one.
    public Type EventsCollectionType => typeof(Events);

    public Type UnknownAggregateExceptionType => typeof(UnknownAggregateException);

    // Core never writes "session.Events.FetchForWriting<T>(...)" itself. Handing back a finished frame
    // is what keeps that spelling - and Marten-only extras like IBatchableFrame enlistment, which
    // Polecat has no equivalent of - entirely on this side of the seam.
    public Frame BuildLoadAggregateFrame(AggregateLoadRequest request) => new LoadAggregateFrame(request);

    // GH-3627. Marten's spelling of the raw stream reads. The batch-query enlistment lives inside these
    // frames, which is the whole reason the seam hands back a Frame rather than letting core write the call.
    public Frame BuildFetchStreamStateFrame(Variable identity) => new FetchStreamStateFrame(identity);

    public Frame BuildFetchStreamFrame(Variable identity) => new FetchStreamFrame(identity);

    public Frame BuildFetchLatestFrame(Type aggregateType, Variable identity)
        => new FetchLatestAggregateFrame(aggregateType, identity);

    public Frame BuildLoadBoundaryFrame(Type modelType) => new LoadBoundaryFrame(modelType);

    public StreamIdentity DetermineStreamIdentity(IServiceContainer container)
        => container.GetInstance<IDocumentStore>().Options.Events.StreamIdentity;

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "The aggregate type comes from handler discovery, which already roots it. Codegen-time only. See docs/guide/aot.md.")]
    public Type? TryDetermineNaturalKeyType(Type aggregateType, IChain chain, IServiceContainer container)
    {
        if (resolveStoreOptions(chain, container) is not { } storeOptions) return null;

        return storeOptions.Projections.FindNaturalKeyDefinition(aggregateType)?.OuterType;
    }

    /// <summary>
    /// The <see cref="StoreOptions"/> of the store this chain writes to. GH-4439.
    /// </summary>
    /// <remarks>
    /// Marten's <c>FindNaturalKeyDefinition</c> searches one store's registered projections, so asking the
    /// default <see cref="IDocumentStore"/> for an aggregate registered only on an ancillary store returns
    /// null and the natural-key branch is silently never taken. A store type this integration does not own —
    /// a Polecat marker on a chain that reached Marten's catch-all <c>CanPersist</c> — falls through to the
    /// default store, which is exactly what happened before the chain was available here.
    /// </remarks>
    private static StoreOptions? resolveStoreOptions(IChain chain, IServiceContainer container)
    {
        if (chain.DetermineAncillaryStoreType() is { } storeType && storeType.CanBeCastTo<IDocumentStore>())
        {
            return (container.Services.GetService(storeType) as IDocumentStore)?.Options as StoreOptions;
        }

        return container.GetInstance<IDocumentStore>().Options as StoreOptions;
    }
}
