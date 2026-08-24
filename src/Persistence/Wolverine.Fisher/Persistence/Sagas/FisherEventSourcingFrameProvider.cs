using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Fisher;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Fisher.Codegen;

namespace Wolverine.Fisher.Persistence.Sagas;

// GH-3907: the Fisher half of the shared aggregate handler workflow's store seam. Deliberately a
// sibling of IPersistenceFrameProvider's contract rather than more members on it, so stores with no
// event sourcing never grow no-op aggregate members - but implemented on the *same class*, which is
// what lets Wolverine find it through the persistence strategies already registered on
// GenerationRules instead of a second registry that would have to know Fisher exists.
internal partial class FisherPersistenceFrameProvider : IEventSourcingFrameProvider
{
    public string StoreName => "Fisher";

    // Wolverine.Fisher.Events and UnknownAggregateException stay public and store-side - GH-3907
    // retires nothing. The workflow only needs to recognize them, so they come over the seam rather
    // than core naming either one.
    public Type EventsCollectionType => typeof(Events);

    public Type UnknownAggregateExceptionType => typeof(UnknownAggregateException);

    // Core never writes "session.Events.FetchForWriting<T>(...)" itself. Handing back a finished frame
    // is what keeps that spelling on this side of the seam - and lets Fisher's frame stay a plain
    // AsyncFrame where Marten's also implements IBatchableFrame.
    public Frame BuildLoadAggregateFrame(AggregateLoadRequest request) => new LoadAggregateFrame(request);

    // GH-3627. Fisher's spelling of the raw stream reads.
    public Frame BuildFetchStreamStateFrame(Variable identity) => new Codegen.FetchStreamStateFrame(identity);

    public Frame BuildFetchStreamFrame(Variable identity) => new Codegen.FetchStreamFrame(identity);

    public Frame BuildFetchLatestFrame(Type aggregateType, Variable identity)
        => new FetchLatestAggregateFrame(aggregateType, identity);

    public Frame BuildLoadBoundaryFrame(Type modelType) => new LoadBoundaryFrame(modelType);

    public StreamIdentity DetermineStreamIdentity(IServiceContainer container)
        => container.Services.GetRequiredService<StoreOptions>().Events.StreamIdentity;

    // Fisher has no natural-key projections, so the workflow simply reports that it could not
    // determine a model id rather than being handed a second place to look. Inheriting the seam's
    // default null is the whole implementation.
}
