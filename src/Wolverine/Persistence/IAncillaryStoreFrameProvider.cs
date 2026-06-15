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
}
