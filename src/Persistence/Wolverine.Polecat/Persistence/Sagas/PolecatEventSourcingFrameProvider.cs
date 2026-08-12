using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat.Codegen;

namespace Wolverine.Polecat.Persistence.Sagas;

// GH-3907: the Polecat half of the shared aggregate handler workflow's store seam. Deliberately a
// sibling of IPersistenceFrameProvider's contract rather than more members on it, so stores with no
// event sourcing never grow no-op aggregate members - but implemented on the *same class*, which is
// what lets Wolverine find it through the persistence strategies already registered on
// GenerationRules instead of a second registry that would have to know Polecat exists.
internal partial class PolecatPersistenceFrameProvider : IEventSourcingFrameProvider
{
    public string StoreName => "Polecat";

    // Wolverine.Polecat.Events and UnknownAggregateException stay public and store-side - GH-3907
    // retires nothing. The workflow only needs to recognize them, so they come over the seam rather
    // than core naming either one.
    public Type EventsCollectionType => typeof(Events);

    public Type UnknownAggregateExceptionType => typeof(UnknownAggregateException);

    // Core never writes "session.Events.FetchForWriting<T>(...)" itself. Handing back a finished frame
    // is what keeps that spelling on this side of the seam - and lets Polecat's frame stay a plain
    // AsyncFrame where Marten's also implements IBatchableFrame.
    public Frame BuildLoadAggregateFrame(AggregateLoadRequest request) => new LoadAggregateFrame(request);

    public Frame BuildFetchLatestFrame(Type aggregateType, Variable identity)
        => new FetchLatestAggregateFrame(aggregateType, identity);

    public StreamIdentity DetermineStreamIdentity(IServiceContainer container)
        => container.Services.GetRequiredService<StoreOptions>().Events.StreamIdentity;

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "The aggregate type comes from handler discovery, which already roots it. Codegen-time only. See docs/guide/aot.md.")]
    public Type? TryDetermineNaturalKeyType(Type aggregateType, IServiceContainer container)
        => container.Services.GetRequiredService<StoreOptions>().Projections
            .FindNaturalKeyDefinition(aggregateType)?.OuterType;
}
