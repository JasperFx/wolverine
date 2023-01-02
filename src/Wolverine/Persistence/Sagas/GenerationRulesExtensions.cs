using System.Collections.Generic;
using System.Linq;
using JasperFx.CodeGeneration;
using Lamar;
using Wolverine.Configuration;

namespace Wolverine.Persistence.Sagas;

public static class GenerationRulesExtensions
{
    public static readonly string SAGA_PERSISTENCE = "SAGA_PERSISTENCE";
    public static readonly string TRANSACTIONS = "TRANSACTIONS";

    private static readonly ISagaPersistenceFrameProvider _nulloSagas = new InMemorySagaPersistenceFrameProvider();
    private static readonly ITransactionFrameProvider _nullo = new NulloTransactionFrameProvider();

    /// <summary>
    ///     The currently known strategy for persisting saga state
    /// </summary>
    public static void SetSagaPersistence(this GenerationRules rules, ISagaPersistenceFrameProvider value)
    {
        if (rules.Properties.ContainsKey(SAGA_PERSISTENCE))
        {
            rules.Properties[SAGA_PERSISTENCE] = value;
        }
        else
        {
            rules.Properties.Add(SAGA_PERSISTENCE, value);
        }
    }

    /// <summary>
    ///     The currently known strategy for persisting saga state
    /// </summary>
    public static ISagaPersistenceFrameProvider GetSagaPersistence(this GenerationRules rules)
    {
        if (rules.Properties.TryGetValue(SAGA_PERSISTENCE, out var persistence))
        {
            return (ISagaPersistenceFrameProvider)persistence;
        }

        return _nulloSagas;
    }

    /// <summary>
    ///     The currently known strategy for code generating transaction middleware
    /// </summary>
    public static void AddTransactionStrategy(this GenerationRules rules, ITransactionFrameProvider value)
    {
        if (rules.Properties.TryGetValue(TRANSACTIONS, out var raw) && raw is List<ITransactionFrameProvider> list)
        {
            list.Add(value);
        }
        else
        {
            list = new List<ITransactionFrameProvider>();
            list.Add(value);
            rules.Properties[TRANSACTIONS] = list;
        }
    }

    public static List<ITransactionFrameProvider> TransactionProviders(this GenerationRules rules)
    {
        if (rules.Properties.TryGetValue(TRANSACTIONS, out var raw) &&
            raw is List<ITransactionFrameProvider> list) return list;

        return new List<ITransactionFrameProvider>();
    }

    /// <summary>
    ///     The currently known strategy for code generating transaction middleware
    /// </summary>
    public static ITransactionFrameProvider GetTransactions(this GenerationRules rules, IChain chain,
        IContainer container)
    {
        if (rules.Properties.TryGetValue(TRANSACTIONS, out var raw) && raw is List<ITransactionFrameProvider> list)
        {
            return list.FirstOrDefault(x => x.CanApply(chain, container)) ?? _nullo;
        }

        return _nullo;
    }

    public class NulloTransactionFrameProvider : ITransactionFrameProvider
    {
        public void ApplyTransactionSupport(IChain chain, IContainer container)
        {
            // Nothing
        }

        public bool CanApply(IChain chain, IContainer container)
        {
            return false;
        }
    }
}