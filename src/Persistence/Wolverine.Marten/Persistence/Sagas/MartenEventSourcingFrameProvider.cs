using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Wolverine.Marten.Codegen;
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

    public Frame BuildFetchLatestFrame(Type aggregateType, Variable identity)
        => new FetchLatestAggregateFrame(aggregateType, identity);

    public StreamIdentity DetermineStreamIdentity(IServiceContainer container)
        => container.GetInstance<IDocumentStore>().Options.Events.StreamIdentity;

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "The aggregate type comes from handler discovery, which already roots it. Codegen-time only. See docs/guide/aot.md.")]
    public Type? TryDetermineNaturalKeyType(Type aggregateType, IServiceContainer container)
    {
        if (container.GetInstance<IDocumentStore>().Options is not StoreOptions storeOptions) return null;

        return storeOptions.Projections.FindNaturalKeyDefinition(aggregateType)?.OuterType;
    }
}
