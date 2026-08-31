using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;

namespace Wolverine.AmazonS3.Internals;

/// <summary>
/// Reads and writes registered document types as S3 objects, so a plain <c>[Entity]</c> parameter and
/// the declarative <c>Storage.Store()</c> / <c>Delete()</c> return values work against a bucket.
/// </summary>
/// <remarks>
/// Built by <c>InsertFirstPersistenceStrategy&lt;T&gt;()</c>, which needs a parameterless constructor,
/// so the configuration is read back out of the container at codegen time.
/// </remarks>
public class S3PersistenceFrameProvider : IPersistenceFrameProvider
{
    /// <summary>
    /// Selective, not catch-all: this claims only the types the application registered, so it is
    /// consulted ahead of catch-all stores like Marten and never competes with them for their own
    /// documents.
    /// </summary>
    public bool IsCatchAll => false;

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IS3DocumentSession);

        return tryFindConfiguration(container, out var configuration) &&
               configuration.TryFindMapping(entityType, out _);
    }

    /// <summary>
    /// Saga chains only, and only for a type registered through <c>Saga&lt;T&gt;()</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the question a saga chain asks -- "which provider owns this CHAIN" -- and the
    ///         answer has to be yes for an S3-persisted saga, or <c>SagaChain</c> falls through to the
    ///         default in <c>GenerationRulesExtensions</c>, which is the IN-MEMORY saga persistor.
    ///     </para>
    ///     <para>
    ///         It stays false for everything else on purpose. <c>[Transactional]</c> and
    ///         <c>AutoApplyTransactions</c> ask the same question, and S3 has no transaction and no unit
    ///         of work -- so an ordinary chain that merely touches an S3 document must never resolve to
    ///         this provider as its transaction owner. See GH-4160.
    ///     </para>
    /// </remarks>
    public bool CanApply(IChain chain, IServiceContainer container)
    {
        return chain is SagaChain saga
               && tryFindConfiguration(container, out var configuration)
               && configuration.TryFindSagaMapping(saga.SagaType, out _);
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
    }

    /// <summary>
    /// The identity type from the document registration. Deliberately not a hardcoded <c>string</c>: a
    /// message handler binds the identity by exact CLR type, so hardcoding it would stop a
    /// Guid-identified document binding at all.
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
        return new LoadS3DocumentFrame(sagaType, sagaId);
    }

    // Insert, update and store are one PutObject; S3 has no insert-versus-update.
    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container) => store(saga);

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container) => store(saga);

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container) => store(saga);

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container) => delete(saga);

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container) => delete(variable);

    /// <summary>
    /// Nothing to commit: every write already happened, and for a saga it happened as a conditional put
    /// that either took effect or threw <c>SagaConcurrencyException</c>.
    /// </summary>
    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        return new CommentFrame("S3 writes take effect immediately; there is no unit of work to commit");
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        return new S3WriteFrame(nameof(S3StorageActionApplier.ApplyAction), entityType, action);
    }

    /// <summary>
    /// S3 has no soft delete; a mapping that wants one answers null from its own serializer.
    /// </summary>
    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];

    // TryBuildAllFrame, TryBuildFirstOrDefaultFrame, TryBuildQueryableFrame and
    // TryBuildFetchSpecificationFrame stay at their defaults of false. ListObjectsV2 over a key prefix
    // is a paged scan, not a query, so [All] / [Queryable] failing at bootstrapping and naming this
    // provider beats a scan that looks like a query until the bucket grows.

    private static Frame store(Variable document)
    {
        return new S3WriteFrame(nameof(S3StorageActionApplier.StoreAsync), document.VariableType, document);
    }

    private static Frame delete(Variable document)
    {
        return new S3WriteFrame(nameof(S3StorageActionApplier.DeleteAsync), document.VariableType, document);
    }

    // Absent for an application that wired this provider up by hand, which registered no documents either.
    private static bool tryFindConfiguration(IServiceContainer container, out AmazonS3Configuration configuration)
    {
        configuration = container.Services.GetService<AmazonS3Configuration>()!;
        return configuration != null;
    }
}
