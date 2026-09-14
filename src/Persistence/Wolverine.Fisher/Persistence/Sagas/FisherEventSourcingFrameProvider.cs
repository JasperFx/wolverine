using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using Microsoft.Extensions.DependencyInjection;
using Fisher;
using Wolverine.Configuration;
using Wolverine.Persistence;
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

    /// <summary>
    /// The natural-key type this aggregate is identified by on the store this chain writes to. GH-4439.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to inherit the seam's null default, on the claim that Fisher has no natural-key concept.
    /// That stopped being true at fisher#40, and the stub was the only thing hiding it: Fisher's
    /// <c>LoadAggregateFrame</c> has carried an <c>IsNaturalKey</c> branch the whole time, so returning null
    /// here meant the branch was unreachable and the whole workflow reported "unable to determine an
    /// aggregate id" for a perfectly well formed handler — on the primary store as much as an ancillary one.
    /// </para>
    /// <para>
    /// The definitions are read off the registered aggregate projections rather than through Fisher's own
    /// <c>FisherProjectionOptions.NaturalKeyFor</c>, which is <c>internal</c> and visible only to Fisher's own
    /// test assemblies — where Marten and Polecat both expose a public <c>FindNaturalKeyDefinition</c>. The
    /// route here is entirely public API: <c>ProjectionGraph.All</c> plus JasperFx's
    /// <see cref="IAggregateProjection.NaturalKeyDefinition" />, which is where the discovery put them anyway.
    /// </para>
    /// <para>
    /// Core's single generated spelling — <c>FetchForWriting&lt;T, TKey&gt;(key, token)</c> — is correct for
    /// Fisher without a frame change: Fisher's overload of it routes to <c>FetchForWritingByNaturalKey</c>
    /// whenever the aggregate declares a key.
    /// </para>
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "The aggregate type comes from handler discovery, which already roots it. Codegen-time only. See docs/guide/aot.md.")]
    public Type? TryDetermineNaturalKeyType(Type aggregateType, IChain chain, IServiceContainer container)
        => resolveStoreOptions(chain, container)?.Projections.All
            .OfType<IAggregateProjection>()
            .Select(x => x.NaturalKeyDefinition)
            .FirstOrDefault(x => x?.AggregateType == aggregateType)?.OuterType;

    /// <summary>
    /// The <see cref="StoreOptions"/> of the store this chain writes to, on the same terms as the Marten and
    /// Polecat twins: a natural key is registered per store, so asking the default store for an aggregate
    /// registered only on an ancillary one silently skips the natural-key branch.
    /// </summary>
    private static StoreOptions? resolveStoreOptions(IChain chain, IServiceContainer container)
    {
        if (chain.DetermineAncillaryStoreType() is { } storeType && storeType.CanBeCastTo<IDocumentStore>())
        {
            return (container.Services.GetService(storeType) as IDocumentStore)?.Options;
        }

        return container.Services.GetRequiredService<StoreOptions>();
    }
}
