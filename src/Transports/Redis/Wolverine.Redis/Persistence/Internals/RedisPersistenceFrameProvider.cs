using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Redis.Internal;

/// <summary>
/// Reads and writes registered types as Redis keys, so a plain <c>[Entity]</c> parameter, the
/// declarative <c>Storage.Store()</c> / <c>Delete()</c> return values, and saga state all resolve
/// against Redis.
/// </summary>
/// <remarks>
/// Built by <c>InsertFirstPersistenceStrategy&lt;T&gt;()</c>, which needs a parameterless constructor,
/// so the configuration is read back out of the container at codegen time.
/// </remarks>
public class RedisPersistenceFrameProvider : IPersistenceFrameProvider
{
    /// <summary>
    /// Selective, not catch-all: this claims only the types the application registered, so it is
    /// consulted ahead of catch-all stores like Marten and never competes with them for their own
    /// documents.
    /// </summary>
    public bool IsCatchAll => false;

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IRedisDocumentSession);

        return tryFindConfiguration(container, out var configuration) &&
               configuration.TryFindMapping(entityType, out _);
    }

    /// <summary>
    /// Saga chains for registered saga types, and nothing else.
    /// </summary>
    /// <remarks>
    /// This is the same question <c>[Transactional]</c> and <c>AutoApplyTransactions</c> ask, and Redis
    /// has no transaction for Wolverine to own: claiming an ordinary chain would make this provider its
    /// transaction owner and quietly take that role away from the store that actually has one. Sagas
    /// are the one case where the chain genuinely belongs here, because <c>Saga&lt;T&gt;()</c> was an
    /// explicit registration of exactly this type.
    /// </remarks>
    public bool CanApply(IChain chain, IServiceContainer container)
    {
        return chain is SagaChain saga
               && tryFindConfiguration(container, out var configuration)
               && configuration.IsRegisteredSaga(saga.SagaType);
    }

    /// <summary>
    /// Nothing to apply. There is no transaction to open and no unit of work to flush, and the saga
    /// frames below carry their own compare-and-swap.
    /// </summary>
    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
    }

    /// <summary>
    /// The identity type from the registration. Deliberately not a hardcoded <c>string</c>: a message
    /// handler binds the identity by exact CLR type, so hardcoding it would stop a Guid-identified
    /// document binding at all.
    /// </summary>
    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        return tryFindConfiguration(container, out var configuration) &&
               configuration.TryFindMapping(sagaType, out var mapping)
            ? mapping.ResolvedIdentityType
            : typeof(string);
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        // A saga read has to bring its revision back with it, or the write that follows has nothing to
        // compare against.
        return isRegisteredSaga(sagaType, container)
            ? new LoadRedisSagaFrame(sagaType, sagaId)
            : new LoadRedisDocumentFrame(sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        return usesOptimisticConcurrency(saga, container)
            ? new RedisSagaInsertFrame(saga)
            : store(saga);
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return usesOptimisticConcurrency(saga, container)
            ? new RedisSagaUpdateFrame(saga)
            : store(saga);
    }

    // Storage.Store() is an explicit "just write it" side effect rather than the saga update path, so
    // it deliberately stays last-write-wins.
    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container) => store(saga);

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return usesOptimisticConcurrency(saga, container)
            ? new RedisSagaDeleteFrame(sagaId, saga)
            : delete(saga);
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container) => delete(variable);

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        return new CommentFrame("Redis writes take effect immediately; there is no unit of work to commit");
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        return new RedisWriteFrame(nameof(RedisStorageActionApplier.ApplyAction), entityType, action);
    }

    /// <summary>
    /// Redis has no soft delete; a mapping that wants one answers null from its own serializer.
    /// </summary>
    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];

    // TryBuildAllFrame, TryBuildFirstOrDefaultFrame, TryBuildQueryableFrame and
    // TryBuildFetchSpecificationFrame stay at their defaults of false. SCAN over a key pattern is a
    // cursor walk of the whole keyspace, not a query, so [All] / [Queryable] failing at bootstrapping
    // and naming this provider beats a scan that looks like a query until the keyspace grows.

    /// <summary>
    /// Compare-and-swap applies to registered sagas, and only to a saga Wolverine read into a local of
    /// its own — that read is what declares the revision the write depends on. Storage actions hand the
    /// provider a synthetic member access like <c>update1.Entity</c> with no preceding read, so they
    /// stay last-write-wins.
    /// </summary>
    private static bool usesOptimisticConcurrency(Variable variable, IServiceContainer container)
    {
        return !variable.Usage.Contains('.') && isRegisteredSaga(variable.VariableType, container);
    }

    private static bool isRegisteredSaga(Type type, IServiceContainer container)
    {
        return tryFindConfiguration(container, out var configuration) && configuration.IsRegisteredSaga(type);
    }

    private static Frame store(Variable document)
    {
        return new RedisWriteFrame(nameof(RedisStorageActionApplier.StoreAsync), document.VariableType, document);
    }

    private static Frame delete(Variable document)
    {
        return new RedisWriteFrame(nameof(RedisStorageActionApplier.DeleteAsync), document.VariableType, document);
    }

    // Absent for an application that wired this provider up by hand, which registered nothing either.
    private static bool tryFindConfiguration(IServiceContainer container,
        out RedisPersistenceConfiguration configuration)
    {
        configuration = container.Services.GetService<RedisPersistenceConfiguration>()!;
        return configuration != null;
    }
}
