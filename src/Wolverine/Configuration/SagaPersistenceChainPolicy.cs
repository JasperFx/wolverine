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
        var providers = rules.PersistenceProviders();

        foreach (var chain in chains)
        {
            var returnedSagas = chain.ReturnVariablesOfType<Saga>();
            foreach (var saga in returnedSagas)
            {
                if (!attachSagaPersistenceFrame(container, providers, saga, chain))
                {
                    throw new InvalidSagaException(
                        "No known Saga persistence provider 'knows' how to insert an entity of type " +
                        saga.VariableType.FullNameInCode() + " referenced in chain " + chain);
                }
            }
        }
    }

    private static bool attachSagaPersistenceFrame(IServiceContainer container, List<IPersistenceFrameProvider> providers,
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