using JasperFx.CodeGeneration.Frames;

namespace Wolverine.Persistence;

/// <summary>
/// Implemented by a Wolverine persistence integration (Marten, Polecat, EF Core, ...) to teach the
/// generic <see cref="StorageAttribute"/> how to route a handler chain to an ancillary
/// (secondary) store owned by that integration. Each integration registers exactly one of these in
/// the IoC container when its <c>IntegrateWithWolverine()</c> is called, so a single
/// <c>[Storage(typeof(IMyStore))]</c> attribute can resolve the right provider purely from the
/// store marker type.
/// </summary>
public interface IAncillaryStoreFrameProvider
{
    /// <summary>
    /// Does this provider own the supplied ancillary store marker type? For example, the Marten
    /// integration returns <c>true</c> when <paramref name="storeType"/> is castable to Marten's
    /// <c>IDocumentStore</c>, and the Polecat integration for Polecat's <c>IDocumentStore</c>.
    /// </summary>
    bool Matches(Type storeType);

    /// <summary>
    /// Build the codegen <see cref="Frame"/> that resolves the ancillary store's
    /// outbox-enrolled session factory and exposes it (as the non-generic factory variable) for the
    /// downstream session-opening frame. Inserted at the front of the chain's middleware so the
    /// handler opens and commits through that store rather than the primary store.
    /// </summary>
    Frame BuildOutboxFactoryFrame(Type storeType);

    /// <summary>
    /// This integration's event sourcing seam, when it has one. Lets a parameter attribute that has no
    /// aggregate type to resolve through -- <c>[StreamState]</c> / <c>[StreamEvents]</c>, GH-3627 -- find
    /// the right store when a chain has been routed to an ancillary one by <see cref="StorageAttribute"/>.
    /// </summary>
    /// <remarks>
    /// Optional and defaulted to null, on the same terms as the optional members of
    /// <see cref="EventSourcing.IEventSourcingFrameProvider"/>: an integration with no event sourcing
    /// (EF Core) inherits the default and the caller reports that rather than failing obscurely.
    /// </remarks>
    EventSourcing.IEventSourcingFrameProvider? EventSourcing => null;
}
