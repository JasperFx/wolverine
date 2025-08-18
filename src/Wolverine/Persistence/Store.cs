using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

/// <summary>
/// Return side effect value that upserts the wrapped entity into the underlying persistence mechanism
/// </summary>
/// <param name="Entity"></param>
/// <typeparam name="T"></typeparam>
public record Store<T>(T Entity) : ISideEffectAware, IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            provider.ApplyTransactionSupport(chain, container, typeof(T));
            var value = new EntityVariable(variable);
            return provider.DetermineStoreFrame(value, container).WrapIfNotNull(variable);
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));
    }

    public StorageAction Action => StorageAction.Store;
}