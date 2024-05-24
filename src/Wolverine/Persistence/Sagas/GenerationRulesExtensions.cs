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

    public static List<IPersistenceFrameProvider> PersistenceProviders(this GenerationRules rules)
    {
        if (rules.Properties.TryGetValue(PersistenceKey, out var raw) &&
            raw is List<IPersistenceFrameProvider> list)
        {
            return list;
        }

        return [];
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