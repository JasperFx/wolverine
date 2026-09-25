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
/// Return side effect value that updates the wrapped entity into the underlying persistence mechanism
/// </summary>
/// <param name="Entity"></param>
/// <typeparam name="T"></typeparam>
public record Update<T>(T Entity) : ISideEffectAware, IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            provider.ApplyTransactionSupport(chain, container, typeof(T));
            var value = new EntityVariable(variable);

            // GH-4613: deliberately NOT DetermineUpdateFrame. The entity here is whatever the handler
            // returned, with no preceding read to track it or to declare a version against, which is a
            // different job from updating a saga Wolverine just loaded.
            var frame = provider.DetermineStorageUpdateFrame(value, container).WrapIfNotNull(variable);
            return frame;
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));
    }
    
    public StorageAction Action => StorageAction.Update;
}