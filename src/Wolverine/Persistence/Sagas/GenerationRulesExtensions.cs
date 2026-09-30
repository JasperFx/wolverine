using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     The outcome of looking a persistence frame provider up by its implementation type with
///     <see cref="GenerationRulesExtensions.TryFindPersistenceFrameProviderOfType" />.
/// </summary>
public enum PersistenceProviderResolution
{
    /// <summary>
    ///     The named provider is registered and claims the entity type.
    /// </summary>
    Found,

    /// <summary>
    ///     No provider of the named type is registered with this application at all — the integration was never
    ///     added.
    /// </summary>
    ProviderNotRegistered,

    /// <summary>
    ///     The named provider is registered, but its <see cref="IPersistenceFrameProvider.CanPersist" /> declines
    ///     this entity type.
    /// </summary>
    ProviderCannotPersistType
}

public static class GenerationRulesExtensions
{
    public static readonly string PersistenceKey = "PERSISTENCE";

    private static readonly IPersistenceFrameProvider _nullo = new InMemoryPersistenceFrameProvider();

    /// <summary>
    ///     The currently known strategy for code generating transaction middleware
    /// </summary>
    public static void AddPersistenceStrategy<T>(this GenerationRules rules) where T : IPersistenceFrameProvider, new()
    {
        if (rules.Properties.TryGetValue(PersistenceKey, out var raw) && raw is List<IPersistenceFrameProvider> list)
        {
            if (!list.OfType<T>().Any())
            {
                list.Add(new T());
            }
        }
        else
        {
            list = [new T()];
            rules.Properties[PersistenceKey] = list;
        }
    }
    
    /// <summary>
    ///     The currently known strategy for code generating transaction middleware, but give this strategy
    /// precedence
    /// </summary>
    public static void InsertFirstPersistenceStrategy<T>(this GenerationRules rules) where T : IPersistenceFrameProvider, new()
    {
        if (rules.Properties.TryGetValue(PersistenceKey, out var raw) && raw is List<IPersistenceFrameProvider> list)
        {
            if (!list.OfType<T>().Any())
            {
                list.Insert(0, new T());
            }
        }
        else
        {
            list = [new T()];
            rules.Properties[PersistenceKey] = list;
        }
    }

    /// <summary>
    /// Tries to find a persistence frame provider for the given entityType
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="container"></param>
    /// <param name="entityType"></param>
    /// <param name="provider"></param>
    /// <returns></returns>
    public static bool TryFindPersistenceFrameProvider(this GenerationRules rules, IServiceContainer container, Type entityType,
        out IPersistenceFrameProvider provider)
    {
        provider = default!;
        var providers = rules.OrderedPersistenceProviders();
        if (providers.Any())
        {
            var candidates = providers.Where(x => x.CanPersist(entityType, container, out var _)).ToArray();

            if (candidates.Any())
            {
                provider = candidates.First();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Finds the registered <see cref="IPersistenceFrameProvider" /> of a <b>named implementation type</b>, as the
    ///     explicit per-provider entity attributes (<c>[FromMarten]</c>, <c>[FromEfCore]</c>, ...) do, and reports
    ///     which of the two possible failures happened.
    /// </summary>
    /// <remarks>
    ///     The distinction between the two failures is the whole point of naming a provider explicitly.
    ///     <see cref="PersistenceProviderResolution.ProviderNotRegistered" /> means the integration is missing from
    ///     the application altogether and the remedy is a bootstrapping call; the entity type is irrelevant.
    ///     <see cref="PersistenceProviderResolution.ProviderCannotPersistType" /> means the integration is there but
    ///     its <see cref="IPersistenceFrameProvider.CanPersist" /> declines this type — for a selective provider
    ///     that usually means the type was never mapped or registered with it. Collapsing the two into one
    ///     "couldn't find a provider" message sends the reader looking in the wrong place.
    /// </remarks>
    public static PersistenceProviderResolution TryFindPersistenceFrameProviderOfType(this GenerationRules rules,
        IServiceContainer container, Type providerType, Type entityType, out IPersistenceFrameProvider provider)
    {
        provider = default!;

        var candidate = rules.PersistenceProviders().FirstOrDefault(providerType.IsInstanceOfType);
        if (candidate == null)
        {
            return PersistenceProviderResolution.ProviderNotRegistered;
        }

        if (!candidate.CanPersist(entityType, container, out _))
        {
            return PersistenceProviderResolution.ProviderCannotPersistType;
        }

        provider = candidate;
        return PersistenceProviderResolution.Found;
    }

    /// <summary>
    ///     The registered persistence providers in deterministic consultation order: selective
    ///     providers (whose CanPersist checks the entity type against their own model, like EF Core)
    ///     ahead of catch-all document stores (whose CanPersist claims any type, like Marten —
    ///     see <see cref="IPersistenceFrameProvider.IsCatchAll"/>), preserving registration order
    ///     within each group. Use this instead of PersistenceProviders() whenever the first matching
    ///     provider wins, so mixed-persistence applications resolve independently of the order in
    ///     which the integrations were registered.
    /// </summary>
    public static IReadOnlyList<IPersistenceFrameProvider> OrderedPersistenceProviders(this GenerationRules rules)
    {
        var providers = rules.PersistenceProviders();
        return providers.Count <= 1 ? providers : providers.OrderBy(x => x.IsCatchAll ? 1 : 0).ToList();
    }

    public static List<IPersistenceFrameProvider> PersistenceProviders(this GenerationRules rules)
    {
        if (rules.Properties.TryGetValue(PersistenceKey, out var raw) &&
            raw is List<IPersistenceFrameProvider> list)
        {
            return list;
        }

        return [_nullo];
    }

    /// <summary>
    ///     The provider that owns this chain's transaction, for the explicit <c>[Transactional]</c> path.
    ///     GH-4631.
    /// </summary>
    /// <remarks>
    ///     <see cref="GetPersistenceProviders" /> answers with the first provider in consultation order,
    ///     which on a chain that depends on two stores is a coin toss the developer never called — the
    ///     other store's buffered writes are dropped with no diagnostic. This asks for a designation
    ///     instead, and fails naming both candidates when there is none. Saga chains keep the
    ///     first-ordered answer, since <see cref="SagaChain" /> resolves its persistence from the saga's
    ///     own state storage rather than from the chain's dependencies.
    /// </remarks>
    internal static IPersistenceFrameProvider SelectTransactionOwner(this GenerationRules rules, IChain chain,
        IServiceContainer container)
    {
        return rules.TrySelectTransactionOwner(chain, container, out var owner) ? owner : _nullo;
    }

    /// <summary>
    ///     <see cref="SelectTransactionOwner" />, but saying whether a real owner was found rather than
    ///     leaving the caller to infer it. GH-4717.
    /// </summary>
    /// <remarks>
    ///     <c>[Transactional]</c> needs to know, because it sets <see cref="IChain.IsTransactional" /> and
    ///     must not claim a transaction the fallback provider never applied (GH-4716). It used to infer that
    ///     from the returned provider's own <c>CanApply</c>, which was sound only while <c>CanApply</c> was
    ///     the single gate. It is not any more: a chain whose store is reachable only through a load
    ///     attribute is claimed through <see cref="DeclarativeLoadDependencies.ProvidersClaiming" /> by a
    ///     provider whose <c>CanApply</c> answers false, and inferring from it would apply a real
    ///     transaction and then report the chain as non-transactional.
    /// </remarks>
    internal static bool TrySelectTransactionOwner(this GenerationRules rules, IChain chain,
        IServiceContainer container, out IPersistenceFrameProvider owner)
    {
        // GH-4717: ProvidersClaiming, not a bare CanApply filter. CanApply answers from ServiceDependencies,
        // which cannot see a store a chain reaches only through [Entity] and its siblings.
        var potentials = rules.OrderedPersistenceProviders().ProvidersClaiming(chain, container);

        if (potentials.Count > 1 && chain is not SagaChain)
        {
            owner = TransactionOwnerResolution.SelectDesignatedOwner(chain, potentials, container);
            return true;
        }

        owner = potentials.FirstOrDefault() ?? _nullo;
        return potentials.Count != 0;
    }

    /// <summary>
    ///     The currently known strategy for code generating transaction middleware
    /// </summary>
    public static IPersistenceFrameProvider GetPersistenceProviders(this GenerationRules rules, IChain chain,
        IServiceContainer container)
    {
        if (rules.Properties.TryGetValue(PersistenceKey, out var raw) && raw is List<IPersistenceFrameProvider>)
        {
            // GH-4717: same reason as SelectTransactionOwner above.
            return rules.OrderedPersistenceProviders().ProvidersClaiming(chain, container).FirstOrDefault() ?? _nullo;
        }

        return _nullo;
    }
}