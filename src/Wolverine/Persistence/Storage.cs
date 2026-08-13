using System.Diagnostics.CodeAnalysis;
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

    /// <summary>
    /// Append events to an existing event stream identified by a Guid. Works against any registered event
    /// store -- Marten, Polecat or Fisher -- because it is expressed purely in terms of
    /// <see cref="JasperFx.Events.IEventOperations"/>.
    /// </summary>
    public static AppendEvents AppendEvents(Guid streamId, params object[] events) => new(streamId, events);

    /// <summary>
    /// Append events to an existing event stream identified by a string key. Works against any registered
    /// event store -- Marten, Polecat or Fisher.
    /// </summary>
    public static AppendEvents AppendEvents(string streamKey, params object[] events) => new(streamKey, events);

    /// <summary>
    /// Append events to an existing event stream identified by a Guid, asserting the stream's current version
    /// on the server for optimistic concurrency.
    /// </summary>
    public static AppendEvents AppendEvents(Guid streamId, long expectedVersion, params object[] events)
        => new(streamId, events) { ExpectedVersion = expectedVersion };

    /// <summary>
    /// Append events to an existing event stream identified by a string key, asserting the stream's current
    /// version on the server for optimistic concurrency.
    /// </summary>
    public static AppendEvents AppendEvents(string streamKey, long expectedVersion, params object[] events)
        => new(streamKey, events) { ExpectedVersion = expectedVersion };

    /// <summary>
    /// Start a brand new event stream with a user supplied Guid identity.
    /// </summary>
    public static StartStream StartStream(Guid streamId, params object[] events) => new(streamId, null, events);

    /// <summary>
    /// Start a brand new event stream with a user supplied string key.
    /// </summary>
    public static StartStream StartStream(string streamKey, params object[] events) => new(streamKey, null, events);

    /// <summary>
    /// Start a brand new event stream for a known aggregate type with a user supplied Guid identity.
    /// </summary>
    public static StartStream StartStream<T>(Guid streamId, params object[] events) where T : class
        => new(streamId, typeof(T), events);

    /// <summary>
    /// Start a brand new event stream for a known aggregate type with a user supplied string key.
    /// </summary>
    public static StartStream StartStream<T>(string streamKey, params object[] events) where T : class
        => new(streamKey, typeof(T), events);

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Variable.VariableType returns the effect's runtime Type without DAM annotation; TypeExtensions.Closes inspects the generic-interface graph for IStorageAction<>. The entity type is application-rooted (handler return type), preserved in any practical setup; AOT consumers register effect entity types via the persistence-frame provider registration.")]
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