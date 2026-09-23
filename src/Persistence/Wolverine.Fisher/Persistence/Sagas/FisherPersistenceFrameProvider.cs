using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Fisher;
using JasperFx.Events;
using Fisher.Events;
using Wolverine.Configuration;
using Wolverine.Fisher.Codegen;
using Wolverine.Fisher.Requirements;
using Wolverine.Persistence;
using Wolverine.Persistence.Codegen;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Fisher.Persistence.Sagas;

internal partial class FisherPersistenceFrameProvider : IPersistenceFrameProvider
{
    // Fisher can persist any document, so CanPersist claims every type. Yield to selective
    // providers (EF Core) for the entity types they actually map, regardless of the order the
    // integrations were registered in
    public bool IsCatchAll => true;

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IDocumentSession);
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        var idProp = sagaType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        return idProp?.PropertyType ?? typeof(Guid);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     GH-4571, the Fisher half of GH-4505. Wolverine's message store here is a second
    ///     <c>SqliteConnection</c> to the same file the document store uses, so the claim is enlisted as an
    ///     <c>ITransactionParticipant</c> — which is handed the live connection and transaction — rather than
    ///     queued as a storage operation the way the Marten twin does. On SQLite that is a correctness
    ///     requirement and not a preference: a write on a second connection blocks on the first one's write
    ///     lock from inside its transaction, which hangs rather than failing.
    ///     <para>
    ///     Fisher has no database-per-tenant composition, so there is no tenancy branch to guard the way the
    ///     Marten and Polecat twins do.
    ///     </para>
    /// </remarks>
    public bool TryBuildTransactionalDeduplication(IChain chain, Variable deduplicationId,
        DeduplicationRequirement requirement, IServiceContainer container,
        [NotNullWhen(true)] out TransactionalDeduplication? deduplication)
    {
        deduplication = null;

        // Not CanApply -- that answers "could this provider own the chain's transaction?", and a chain that
        // merely takes an IDocumentSession satisfies it without ever committing one. Riding a transaction
        // that is never committed writes no claim at all, so the endpoint silently loses deduplication
        // entirely: every replay runs. Ask for the commit frame itself, which is the thing the claim has to
        // ride. See the Marten and Polecat twins.
        if (!chain.Middleware.OfType<CreateDocumentSessionFrame>().Any()) return false;
        if (!chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any()) return false;

        var marker = chain.DetermineAncillaryStoreType();
        var check = new FisherDeduplicationClaimExistsFrame(deduplicationId, marker);

        deduplication = new TransactionalDeduplication
        {
            Check = check,
            IsDuplicate = check.Variable,
            Claim = new QueueFisherDeduplicationClaimFrame(deduplicationId, check.Variable, marker),
            CommitRaceWrapper = new RefuseDuplicateClaimAtCommitFrame(
                FisherDeduplicationFailures.Classifier,
                lost => chain.BuildDeduplicationStopCondition(lost, DeduplicationOutcome.Duplicate, requirement))
        };

        return true;
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (!chain.Middleware.OfType<CreateDocumentSessionFrame>().Any())
        {
            chain.Middleware.Add(new CreateDocumentSessionFrame(chain));
        }

        if (chain is not SagaChain)
        {
            if (!chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any())
            {
                chain.Postprocessors.Add(new DocumentSessionSaveChanges());
            }

            if (!chain.Postprocessors.OfType<FlushOutgoingMessages>().Any())
            {
                chain.Postprocessors.Add(new FlushOutgoingMessages());
            }
        }
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        ApplyTransactionSupport(chain, container);
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain)
        {
            return true;
        }

        if (chain.ReturnVariablesOfType<IFisherOp>().Any()) return true;

        // GH-2941: detect parameter attributes whose Modify() injects a non-MethodCall frame
        // depending on IDocumentSession. See MartenPersistenceFrameProvider.CanApply for the full
        // explanation; Fisher mirrors the Marten path structurally. Pairs with the upstream Fisher
        // fix shipped in Fisher 0.5.3 - found building this integration, and the same defect Polecat
        // fixed in polecat#161: SaveChangesAsync now runs queued ITransactionParticipants even when
        // the unit of work holds no documents and no events, so the StoreIncomingEnvelopeParticipant
        // added via Session.StoreIncoming(...) for a scheduled cascade actually executes inside the
        // chain's session transaction instead of being skipped with the envelope.
        if (ChainHasFisherSessionAttributes(chain)) return true;

        var serviceDependencies = chain
            .ServiceDependencies(container, new[] { typeof(IDocumentSession), typeof(IQuerySession), typeof(IDocumentOperations), typeof(global::JasperFx.Events.IEventOperations), typeof(global::JasperFx.Events.IEventStoreOperations), typeof(global::Fisher.Events.EventOperations), typeof(global::JasperFx.Events.Documents.IDocumentSessionOperations), typeof(global::JasperFx.Events.Documents.IDocumentWriteOperations), typeof(global::JasperFx.Events.Documents.IDocumentReadOperations) }).ToArray();
                // A handler that takes the event operations straight as a parameter -- the shared
        // JasperFx.Events.IEventOperations / IEventStoreOperations, or Fisher's own EventOperations -- is
        // unambiguously using this store, but none of those types appeared here, so
        // AutoApplyTransactions skipped the chain and nothing was ever committed. Appending
        // through the parameter queued into the session's unit of work and then silently
        // vanished, with no exception.
        //
        // GH-3956: same hole on the DOCUMENT side. See MartenPersistenceFrameProvider.CanApply.
        // IDocumentReadOperations is probed but NOT matched, exactly as IQuerySession has always been --
        // a read-only parameter is not evidence that the chain writes anything.
        return serviceDependencies.Any(x => x == typeof(IDocumentSession) || x == typeof(IDocumentOperations)
                                            || x.Closes(typeof(IEventStream<>))
                                            || x == typeof(global::JasperFx.Events.IEventOperations)
                                            || x == typeof(global::JasperFx.Events.IEventStoreOperations)
                                            || x == typeof(global::Fisher.Events.EventOperations)
                                            || x == typeof(global::JasperFx.Events.Documents.IDocumentSessionOperations)
                                            || x == typeof(global::JasperFx.Events.Documents.IDocumentWriteOperations));
    }

    private static bool ChainHasFisherSessionAttributes(IChain chain)
    {
        foreach (var call in chain.HandlerCalls())
        {
            foreach (var parameter in call.Method.GetParameters())
            {
                if (parameter.GetCustomAttributes().Any(a => a is ReadAggregateAttribute)) return true;
            }
        }

        foreach (var call in chain.HandlerCalls())
        {
            if (call.Method.GetCustomAttributes().Any(IsDocumentExistsAttribute)) return true;
            if (call.HandlerType.GetCustomAttributes(true).OfType<Attribute>().Any(IsDocumentExistsAttribute)) return true;
        }

        var messageType = chain.InputType();
        if (messageType != null && messageType.GetCustomAttributes(true).OfType<Attribute>().Any(IsDocumentExistsAttribute))
        {
            return true;
        }

        return false;
    }

    private static bool IsDocumentExistsAttribute(Attribute attribute)
    {
        var type = attribute.GetType();
        if (!type.IsGenericType) return false;
        var def = type.GetGenericTypeDefinition();
        return def == typeof(DocumentExistsAttribute<>) || def == typeof(DocumentDoesNotExistAttribute<>);
    }

    public bool TryBuildAllFrame(Type entityType, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame,
        [NotNullWhen(true)] out Variable? result)
    {
        var all = new AllFrame(entityType);
        frame = all;
        result = all.Result;
        return true;
    }

    public bool TryBuildFirstOrDefaultFrame(Type entityType, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame,
        [NotNullWhen(true)] out Variable? result)
    {
        var first = new FirstOrDefaultFrame(entityType);
        frame = first;
        result = first.Result;
        return true;
    }

    public bool TryBuildQueryableFrame(Type elementType, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame,
        [NotNullWhen(true)] out Variable? result)
    {
        var queryable = new QueryableFrame(elementType);
        frame = queryable;
        result = queryable.Result;
        return true;
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        return new LoadDocumentFrame(sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Insert));
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        return new DocumentSessionSaveChanges();
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Update));
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Delete));
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Store));
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(variable, nameof(IDocumentSession.Delete));
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var method = typeof(FisherStorageActionApplier).GetMethod("ApplyAction")!
            .MakeGenericMethod(entityType);

        var call = new MethodCall(typeof(FisherStorageActionApplier), method);
        call.Arguments[1] = action;

        return call;
    }

    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)
    {
        // Fisher doesn't have DocumentMetadata for soft-delete check in the same way,
        // so we skip this for now
        return [];
    }
}

public static class FisherStorageActionApplier
{
    public static void ApplyAction<T>(IDocumentSession session, IStorageAction<T> action) where T : notnull
    {
        if (action.Entity == null) return;

        switch (action.Action)
        {
            case StorageAction.Delete:
                session.Delete(action.Entity);
                break;
            case StorageAction.Insert:
                session.Insert(action.Entity);
                break;
            case StorageAction.Store:
                session.Store(action.Entity);
                break;
            case StorageAction.Update:
                session.Update(action.Entity);
                break;
        }
    }
}

internal class DocumentSessionSaveChanges : MethodCall
{
    public DocumentSessionSaveChanges() : base(typeof(IDocumentSession), ReflectionHelper.GetMethod<IDocumentSession>(x => x.SaveChangesAsync(default))!)
    {
        CommentText = "Save all pending changes to this Fisher session";
    }
}
