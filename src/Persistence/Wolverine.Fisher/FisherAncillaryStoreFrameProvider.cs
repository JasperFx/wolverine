using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Fisher;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Fisher.Codegen;

namespace Wolverine.Fisher;

/// <summary>
/// Teaches the generic <see cref="StorageAttribute"/> (<c>[Storage(typeof(IMyStore))]</c>) how to
/// route a handler to a Fisher ancillary store. Registered when Fisher is integrated with Wolverine.
/// </summary>
internal class FisherAncillaryStoreFrameProvider : IAncillaryStoreFrameProvider
{
    public bool Matches(Type storeType) => storeType.CanBeCastTo<IDocumentStore>();

    public Frame BuildOutboxFactoryFrame(Type storeType) => new AncillaryOutboxFactoryFrame(storeType);

    // GH-3627. Lets [StreamState] / [StreamEvents] find Fisher when a chain has been routed to a Fisher
    // ancillary store by [Storage(...)] -- those attributes have no aggregate type to resolve through.
    public IEventSourcingFrameProvider? EventSourcing { get; } = new Persistence.Sagas.FisherPersistenceFrameProvider();
}
