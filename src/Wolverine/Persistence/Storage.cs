using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

/// <summary>
/// Convenience class to build storage actions for return values on Wolverine handler methods
/// or http endpoint methods
/// </summary>
public static class Storage
{
    /// <summary>
    /// "Upsert" an entity. Note that not every persistence tool natively supports
    /// upsert operations
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Store<T> Store<T>(T entity) => new(entity);
    
    /// <summary>
    /// "Insert" a new entity to the underlying persistence mechanism
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Insert<T> Insert<T>(T entity) => new(entity);
    
    /// <summary>
    /// "Update" the entity into the underlying persistence mechanism
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Update<T> Update<T>(T entity) => new(entity);
    
    /// <summary>
    /// "Delete" the entity in the underlying persistence mechanism
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Delete<T> Delete<T>(T entity) => new(entity);
    
    /// <summary>
    /// Do absolutely nothing with this entity
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Nothing<T> Nothing<T>() => new();

    internal static bool TryApply(Variable effect, GenerationRules rules, IServiceContainer container, IChain chain)
    {
        if (effect.VariableType.Closes(typeof(IStorageAction<>)) &&
            effect.VariableType.GetGenericTypeDefinition() == typeof(IStorageAction<>))
        {
            var entityType = effect.VariableType.GetGenericArguments()[0];
            if (rules.TryFindPersistenceFrameProvider(container, entityType, out var provider))
            {
                effect.UseReturnAction(v => provider.DetermineStorageActionFrame(entityType, effect, container).WrapIfNotNull(effect));
                provider.ApplyTransactionSupport(chain, container, entityType);
                return true;
            }

            throw new NoMatchingPersistenceProviderException(entityType);
        }

        return false;
    }
}