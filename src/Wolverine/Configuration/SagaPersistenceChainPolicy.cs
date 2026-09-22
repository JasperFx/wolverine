using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

internal class SagaPersistenceChainPolicy : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var providers = rules.OrderedPersistenceProviders();

        foreach (var chain in chains)
        {
            var returnedSagas = chain.ReturnVariablesOfType<Saga>();
            foreach (var saga in returnedSagas)
            {
                if (!attachSagaPersistenceFrame(container, providers, saga, chain))
                {
                    // GH-4531: name what *does* provide saga persistence, because "no known provider"
                    // is unactionable on its own -- the reader has to know the list to spot what is missing.
                    throw new InvalidSagaException(
                        "No known Saga persistence provider 'knows' how to insert an entity of type " +
                        saga.VariableType.FullNameInCode() + " referenced in chain " + chain +
                        ". Saga state is stored by the message store: PersistMessagesWithPostgresql/SqlServer/MySql/Sqlite/Oracle " +
                        "(lightweight saga tables), IntegrateWithWolverine() on a Marten/Polecat/Fisher store, an EF Core DbContext with a DbSet<" +
                        saga.VariableType.NameInCode() + "> under UseEntityFrameworkCoreTransactions(), or RavenDb/CosmosDb/Redis persistence. " +
                        "Register one that supports sagas.");
                }
            }
        }
    }

    private static bool attachSagaPersistenceFrame(IServiceContainer container, IReadOnlyList<IPersistenceFrameProvider> providers,
        Variable saga, IChain chain)
    {
        foreach (var provider in providers)
        {
            if (provider.CanPersist(saga.VariableType, container, out var serviceType))
            {
                chain.AddDependencyType(serviceType);

                saga.UseReturnAction(v => provider.DetermineInsertFrame(v, container),
                    "Persisting the new Saga entity");

                provider.ApplyTransactionSupport(chain, container);
                return true;
            }
        }

        return false;
    }
}