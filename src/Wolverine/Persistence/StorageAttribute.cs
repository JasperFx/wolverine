using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

/// <summary>
/// Route a handler (or every handler on a class) to an ancillary (secondary) store, regardless of
/// which Wolverine persistence integration owns that store. This is the provider-agnostic sibling of
/// <c>[MartenStore]</c> and <c>[PolecatStore]</c> — apply <c>[Storage(typeof(IMyStore))]</c> once and
/// Wolverine resolves the owning integration (Marten, Polecat, EF Core, ...) from the store marker
/// type via the registered <see cref="IAncillaryStoreFrameProvider"/> instances.
/// </summary>
/// <remarks>
/// The handler will open and commit its work through the targeted store's outbox-enrolled session,
/// and inline-projection side effects relayed by that store flow through the Wolverine outbox — the
/// same behavior as the provider-specific attributes.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class StorageAttribute : ModifyChainAttribute
{
    public Type StoreType { get; }

    public StorageAttribute(Type storeType)
    {
        StoreType = storeType;
    }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        chain.UseAncillaryStorage(StoreType, container);
    }
}

public static class AncillaryStorageChainExtensions
{
    /// <summary>
    /// Route a chain to the ancillary (secondary) store identified by <paramref name="storeType"/>.
    /// Sets <see cref="IChain.AncillaryStoreType"/> and inserts the owning integration's
    /// outbox-factory frame at the front of the chain's middleware. Resolves the owning integration
    /// from the registered <see cref="IAncillaryStoreFrameProvider"/> instances, so it works for any
    /// persistence provider that has been integrated with Wolverine. Callable directly from an
    /// <see cref="IChainPolicy"/> so a whole assembly of handlers can be routed without per-handler
    /// markup.
    /// </summary>
    public static void UseAncillaryStorage(this IChain chain, Type storeType, IServiceContainer container)
    {
        chain.AncillaryStoreType = storeType;

        var provider = container.GetAllInstances<IAncillaryStoreFrameProvider>()
            .FirstOrDefault(x => x.Matches(storeType));

        if (provider == null)
        {
            throw new InvalidOperationException(
                $"No registered Wolverine persistence integration owns the ancillary store type '{storeType.FullNameInCode()}'. " +
                "Be sure you have called IntegrateWithWolverine() on that store (e.g. via AddMartenStore<T>() or AddPolecatStore<T>()).");
        }

        chain.Middleware.Insert(0, provider.BuildOutboxFactoryFrame(storeType));
    }
}
