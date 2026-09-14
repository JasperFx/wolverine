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
/// <summary>
/// An attribute that routes a chain to an ancillary (secondary) store — <see cref="StorageAttribute"/> and
/// each integration's own spelling of it (<c>[MartenStore]</c>, <c>[PolecatStore]</c>, <c>[FisherStore]</c>).
/// </summary>
/// <remarks>
/// GH-4439. These four attributes all carried an identical <c>StoreType</c> property and no common type, so
/// code that needed to answer "which store is this chain routed to?" before the attribute had been applied
/// had nothing to look for. See <see cref="AncillaryStorageChainExtensions.DetermineAncillaryStoreType"/>
/// for why that question gets asked at a point where <see cref="IChain.AncillaryStoreType"/> may still be null.
/// </remarks>
public interface IAncillaryStoreAttribute
{
    Type StoreType { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class StorageAttribute : ModifyChainAttribute, IAncillaryStoreAttribute
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
    /// The ancillary store this chain is routed to, whether or not the routing attribute has been applied
    /// to the chain yet. Null when the chain writes to the default store. GH-4439.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefer this over reading <see cref="IChain.AncillaryStoreType"/> directly from anything that runs
    /// during codegen, because <b>when that property is populated differs by chain type</b>:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// On a <b>handler</b> chain, parameter attributes run BEFORE chain attributes
    /// (<c>HandlerChain.applyCustomizations</c> — "THIS has to go before the baseline attributes"), so the
    /// property is only non-null thanks to the Phase-A eager policies (<c>MartenStoreEagerPolicy</c> and
    /// friends) that pre-assign it at <c>HandlerGraph.Compile</c>.
    /// </item>
    /// <item>
    /// On an <b>HTTP</b> chain it is still null: <c>HttpChain.MapToRoute</c> triggers parameter matching
    /// from the <c>[WolverinePost]</c>-style attribute in the constructor, long before
    /// <c>applyAttributesAndConfigureMethods</c> applies <c>[MartenStore]</c> — and the eager policies are
    /// <c>IHandlerPolicy</c>, so they never see an endpoint at all.
    /// </item>
    /// </list>
    /// <para>
    /// Reading the attribute off the handler type answers the question in both cases without reordering
    /// HTTP chain construction, which the ordering comments there warn against.
    /// </para>
    /// </remarks>
    public static Type? DetermineAncillaryStoreType(this IChain chain)
    {
        if (chain.AncillaryStoreType is { } assigned) return assigned;

        foreach (var call in chain.HandlerCalls())
        {
            var fromMethod = call.Method.GetCustomAttributes(true).OfType<IAncillaryStoreAttribute>()
                .FirstOrDefault();
            if (fromMethod != null) return fromMethod.StoreType;

            var fromType = call.HandlerType.GetCustomAttributes(true).OfType<IAncillaryStoreAttribute>()
                .FirstOrDefault();
            if (fromType != null) return fromType.StoreType;
        }

        return null;
    }

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
