using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

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
        provider = default;
        var providers = rules.PersistenceProviders();
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
    ///     The currently known strategy for code generating transaction middleware
    /// </summary>
    public static IPersistenceFrameProvider GetPersistenceProviders(this GenerationRules rules, IChain chain,
        IServiceContainer container)
    {
        if (rules.Properties.TryGetValue(PersistenceKey, out var raw) && raw is List<IPersistenceFrameProvider> list)
        {
            return list.FirstOrDefault(x => x.CanApply(chain, container)) ?? _nullo;
        }

        return _nullo;
    }
}