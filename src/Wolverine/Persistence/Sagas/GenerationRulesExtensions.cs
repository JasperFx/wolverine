using JasperFx.CodeGeneration;
using Lamar;
using Wolverine.Configuration;

namespace Wolverine.Persistence.Sagas;

public static class GenerationRulesExtensions
{
    public static readonly string SAGA_PERSISTENCE = "SAGA_PERSISTENCE";
    public static readonly string TRANSACTIONS = "TRANSACTIONS";

    private static readonly ISagaPersistenceFrameProvider _nulloSagas = new InMemorySagaPersistenceFrameProvider();
    private static readonly ITransactionFrameProvider _transactions = new NulloTransactionFrameProvider();

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
    public static void SetTransactions(this GenerationRules rules, ITransactionFrameProvider value)
    {
        if (rules.Properties.ContainsKey(TRANSACTIONS))
        {
            rules.Properties[TRANSACTIONS] = value;
        }
        else
        {
            rules.Properties.Add(TRANSACTIONS, value);
        }
    }

    /// <summary>
    ///     The currently known strategy for code generating transaction middleware
    /// </summary>
    public static void SetTransactionsIfNone(this GenerationRules rules, ITransactionFrameProvider value)
    {
        if (!rules.Properties.ContainsKey(TRANSACTIONS))
        {
            rules.Properties.Add(TRANSACTIONS, value);
        }
    }

    /// <summary>
    ///     The currently known strategy for code generating transaction middleware
    /// </summary>
    public static ITransactionFrameProvider GetTransactions(this GenerationRules rules)
    {
        if (rules.Properties.TryGetValue(TRANSACTIONS, out var transactions))
        {
            return (ITransactionFrameProvider)transactions;
        }

        return _transactions;
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