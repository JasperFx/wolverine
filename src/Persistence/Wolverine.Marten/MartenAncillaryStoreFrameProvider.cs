using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Marten;
using Wolverine.Marten.Codegen;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Marten;

/// <summary>
/// Teaches the generic <see cref="StorageAttribute"/> (<c>[Storage(typeof(IMyStore))]</c>) how to
/// route a handler to a Marten ancillary store. Registered when Marten is integrated with Wolverine.
/// </summary>
internal class MartenAncillaryStoreFrameProvider : IAncillaryStoreFrameProvider
{
    public bool Matches(Type storeType) => storeType.CanBeCastTo<IDocumentStore>();

    public Frame BuildOutboxFactoryFrame(Type storeType) => new AncillaryOutboxFactoryFrame(storeType);

    // GH-3627. Lets [StreamState] / [StreamEvents] find Marten when a chain has been routed to a Marten
    // ancillary store by [Storage(...)] -- those attributes have no aggregate type to resolve through.
    public IEventSourcingFrameProvider? EventSourcing { get; } = new Persistence.Sagas.MartenPersistenceFrameProvider();
}
