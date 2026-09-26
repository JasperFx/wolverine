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
/// Return side effect value that inserts the wrapped entity into the underlying persistence mechanism
/// </summary>
/// <param name="Entity"></param>
/// <typeparam name="T"></typeparam>
public record Insert<T>(T Entity) : ISideEffectAware, IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            provider.ApplyTransactionSupport(chain, container, typeof(T));
            var value = new EntityVariable(variable);

            // GH-4613: see DetermineStorageUpdateFrame. Defaults to DetermineInsertFrame, so this is a
            // no-op for every provider that does not need to tell a returned entity from a saga.
            return provider.DetermineStorageInsertFrame(value, container).WrapIfNotNull(variable);
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));
    }
    
    public StorageAction Action => StorageAction.Insert;
}