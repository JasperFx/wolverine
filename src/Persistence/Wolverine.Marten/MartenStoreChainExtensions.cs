using Wolverine.Configuration;
using Wolverine.Marten.Codegen;

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
}
