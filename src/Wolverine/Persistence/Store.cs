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
            // GH-5345: a saga stored this way gets LastModified like one a saga chain saves
            return StampSagaLastModifiedFrame.ForStorageAction(typeof(T), value,
                provider.DetermineStoreFrame(value, container), throughHelper: false).WrapIfNotNull(variable);
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));
    }

    public StorageAction Action => StorageAction.Store;
}