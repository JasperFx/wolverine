using System.Diagnostics.CodeAnalysis;
using System.Net;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using MethodCall = JasperFx.CodeGeneration.Frames.MethodCall;

namespace Wolverine.CosmosDb.Internals;

public class CosmosDbPersistenceFrameProvider : IPersistenceFrameProvider
{
    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (!chain.Middleware.OfType<TransactionalFrame>().Any())
        {
            chain.Middleware.Add(new TransactionalFrame(chain));

            if (chain is not SagaChain)
            {
                chain.Postprocessors.Add(CosmosDbOutboxFlushing.BuildFrame());
            }
        }
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        ApplyTransactionSupport(chain, container);
    }

    /// <summary>
    /// GH-4631. Cosmos DB claims ORDINARY chains — a <see cref="Container"/> dependency or an
    /// <c>ICosmosDbOp</c> return — so it genuinely co-applies with EF Core on a handler that takes both,
    /// and a designation has to be able to resolve here or that handler could not be built at all.
    /// Cosmos DB has no session interface; the thing a handler depends on, and therefore the thing a
    /// designation names, is the SDK's own <see cref="Container"/> / <see cref="Database"/> /
    /// <see cref="CosmosClient"/>.
    /// </summary>
    public bool OwnsStorageType(Type storageType, IServiceContainer container)
    {
        return storageType.CanBeCastTo<Container>()
               || storageType.CanBeCastTo<Database>()
               || storageType.CanBeCastTo<CosmosClient>();
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain)
        {
            return true;
        }

        if (chain.ReturnVariablesOfType<ICosmosDbOp>().Any()) return true;

        var serviceDependencies = chain
            .ServiceDependencies(container, new[] { typeof(Container) }.ToArray());
        return serviceDependencies.Any(x => x == typeof(Container));
    }

    // CosmosDb can persist any document, so CanPersist claims every type. Yield to selective
    // providers (EF Core) for the entity types they actually map, regardless of the order the
    // integrations were registered in
    public bool IsCatchAll => true;

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(Container);
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        return typeof(string);
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
        return new LoadDocumentFrame(sagaType, sagaId, PartitionsById(sagaType, container));
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        // A brand new saga has no ETag to match against, but it must not already exist. Without a
        // conditional create, a second start message for the same saga id would blindly overwrite the
        // saga that is already running
        return new CosmosDbCreateSagaFrame(saga, PartitionsById(saga.VariableType, container));
    }

    // An Insert<T> return value is an explicit "write this entity" side effect, not a saga being
    // started, so it stays the upsert it has always been rather than inheriting the saga create above
    public Frame DetermineStorageInsertFrame(Variable entity, IServiceContainer container)
    {
        return new CosmosDbUpsertFrame(entity, PartitionsById(entity.VariableType, container));
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        // CosmosDB operations are immediate, but we still flush outgoing messages
        return CosmosDbOutboxFlushing.BuildFrame();
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        // Updating a saga document is a compare-and-swap against the ETag captured by LoadDocumentFrame
        // when the saga was read. Without it, two messages for the same saga id racing on separate nodes
        // both upsert blindly and the second one silently overwrites the first. See GH-3414.
        return new CosmosDbUpsertFrame(saga, PartitionsById(saga.VariableType, container),
            LoadDocumentFrame.UsesOptimisticConcurrency(saga));
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new CosmosDbDeleteDocumentFrame(sagaId, saga, PartitionsById(saga.VariableType, container),
            LoadDocumentFrame.UsesOptimisticConcurrency(saga));
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        // Storage.Store() is an explicit "just write it" side effect on an arbitrary document, not the
        // saga update path, so it deliberately stays a blind upsert
        return new CosmosDbUpsertFrame(saga, PartitionsById(saga.VariableType, container));
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return new CosmosDbDeleteByVariableFrame(variable);
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        // A saga written through an explicit storage action has to land in the same partition the saga
        // loader will read it back from, or turning saga partitioning on would quietly hide it (GH-3415)
        var methodName = PartitionsById(entityType, container)
            ? nameof(CosmosDbStorageActionApplier.ApplyPartitionedSagaAction)
            : nameof(CosmosDbStorageActionApplier.ApplyAction);

        var method = typeof(CosmosDbStorageActionApplier).GetMethod(methodName)!
            .MakeGenericMethod(entityType);

        var call = new MethodCall(typeof(CosmosDbStorageActionApplier), method);
        call.Arguments[1] = action;

        return call;
    }

    /// <summary>
    ///     GH-3415. Does this document get a logical partition of its own, keyed by its id? Only sagas, and only
    ///     when the application asked for it with <see cref="CosmosDbConfiguration.PartitionSagasById" />.
    ///     <para>
    ///         Deliberately sagas only. The same frames load and store the arbitrary documents behind
    ///         <c>[Entity]</c> parameters and <c>Storage.Store()</c> side effects, and those may well already carry
    ///         a partition key of the user's own choosing, or be written by the user's own code straight through
    ///         the injected <see cref="Container" />. Wolverine is not entitled to re-home them.
    ///     </para>
    /// </summary>
    internal static bool PartitionsById(Type documentType, IServiceContainer container)
    {
        if (!documentType.CanBeCastTo<Saga>())
        {
            return false;
        }

        // Absent for an application that wired the persistence strategy up by hand rather than through
        // UseCosmosDbPersistence(), in which case it did not ask for saga partitioning either
        var configuration = container.Services.GetService<CosmosDbConfiguration>();
        return configuration is { SagasArePartitionedById: true };
    }
}

public static class CosmosDbStorageActionApplier
{
    public static async Task ApplyAction<T>(Container container, IStorageAction<T> action)
    {
        if (action.Entity == null) return;

        switch (action.Action)
        {
            case StorageAction.Delete:
                // For delete, we need the id. Use ToString() as a convention
                var deleteId = action.Entity!.ToString()!;
                try
                {
                    await container.DeleteItemAsync<T>(deleteId, PartitionKey.None);
                }
                catch (CosmosException)
                {
                    // Best effort
                }

                break;
            case StorageAction.Insert:
            case StorageAction.Store:
            case StorageAction.Update:
                await container.UpsertItemAsync(action.Entity);
                break;
        }
    }

    /// <summary>
    ///     The same storage actions against a saga in an application that opted into
    ///     <see cref="CosmosDbConfiguration.PartitionSagasById" />: every write carries the saga's partition key,
    ///     and the delete is keyed by the saga's own document id rather than <c>ToString()</c>, because that id is
    ///     now the partition to delete from.
    /// </summary>
    public static async Task ApplyPartitionedSagaAction<T>(Container container, IStorageAction<T> action,
        CancellationToken cancellationToken)
    {
        if (action.Entity == null) return;

        switch (action.Action)
        {
            case StorageAction.Delete:
                try
                {
                    await CosmosSagaStorage.DeleteAsync(container, action.Entity, cancellationToken);
                }
                catch (CosmosException)
                {
                    // Best effort, exactly as the unpartitioned delete above
                }

                break;
            case StorageAction.Insert:
            case StorageAction.Store:
            case StorageAction.Update:
                await CosmosSagaStorage.UpsertAsync(container, action.Entity, null, cancellationToken);
                break;
        }
    }
}

/// <summary>
///     Emits the compare-and-swap plumbing that turns a 412 Precondition Failed from CosmosDB into the
///     <see cref="SagaConcurrencyException" /> Wolverine's saga retry machinery already understands. Shared
///     by the saga upsert and delete frames so both report the violation identically. The saga create frame
///     does the same for the 409 Conflict of a saga id that already exists.
/// </summary>
internal static class CosmosDbConcurrency
{
    private const string UpdateViolation = "cannot be updated because of optimistic concurrency violations";
    private const string UpdateComment = "The saga document was changed by another message since it was read";

    private const string CreateViolation = "cannot be started because a saga with that id already exists";
    private const string CreateComment = "Another message already started a saga with this id";

    public static string RequestOptions(string etagVariable)
    {
        return
            $"requestOptions: new {typeof(ItemRequestOptions).FullNameInCode()}{{ IfMatchEtag = {etagVariable} }}";
    }

    public static string FSharpRequestOptions(string etagVariable)
    {
        return
            $"requestOptions = {typeof(ItemRequestOptions).FSharpName()}(IfMatchEtag = {etagVariable})";
    }

    public static void WriteCatchBlock(ISourceWriter writer, Type sagaType, string identityExpression)
    {
        writeCatchBlock(writer, sagaType, identityExpression, nameof(HttpStatusCode.PreconditionFailed),
            UpdateComment, UpdateViolation);
    }

    public static void WriteFSharpCatchBlock(ISourceWriter writer, Type sagaType, string identityExpression)
    {
        writeFSharpCatchBlock(writer, sagaType, identityExpression, nameof(HttpStatusCode.PreconditionFailed),
            UpdateComment, UpdateViolation);
    }

    public static void WriteConflictCatchBlock(ISourceWriter writer, Type sagaType, string identityExpression)
    {
        writeCatchBlock(writer, sagaType, identityExpression, nameof(HttpStatusCode.Conflict), CreateComment,
            CreateViolation);
    }

    public static void WriteFSharpConflictCatchBlock(ISourceWriter writer, Type sagaType, string identityExpression)
    {
        writeFSharpCatchBlock(writer, sagaType, identityExpression, nameof(HttpStatusCode.Conflict), CreateComment,
            CreateViolation);
    }

    private static void writeCatchBlock(ISourceWriter writer, Type sagaType, string identityExpression,
        string statusCode, string comment, string violation)
    {
        writer.Write(
            $"BLOCK:catch ({typeof(CosmosException).FullNameInCode()} e) when (e.StatusCode == System.Net.HttpStatusCode.{statusCode})");
        writer.WriteComment(comment);
        writer.Write(
            $"throw new {typeof(SagaConcurrencyException).FullNameInCode()}($\"Saga of type {sagaType.FullNameInCode()} and id {{{identityExpression}}} {violation}\", e);");
        writer.FinishBlock();
    }

    private static void writeFSharpCatchBlock(ISourceWriter writer, Type sagaType, string identityExpression,
        string statusCode, string comment, string violation)
    {
        writer.Write(
            $"BLOCK:with :? {typeof(CosmosException).FSharpName()} as e when e.StatusCode = System.Net.HttpStatusCode.{statusCode} ->");
        writer.WriteComment(comment);
        writer.Write(
            $"raise ({typeof(SagaConcurrencyException).FSharpName()}(sprintf \"Saga of type {sagaType.FullNameInCode()} and id %O {violation}\" {identityExpression}, e))");
        writer.FinishBlock();
    }
}

/// <summary>
///     Writes a saga that is being started. CosmosDB's create fails with 409 Conflict when a document with the
///     same id already exists, which surfaces as a <see cref="SagaConcurrencyException" /> instead of letting a
///     duplicate start message overwrite a saga that is already running, the same guarantee Marten's insert and
///     the blob stores' <c>If-None-Match: *</c> give.
/// </summary>
internal class CosmosDbCreateSagaFrame : AsyncFrame
{
    private readonly Variable _saga;
    private readonly bool _partitionById;
    private Variable? _cancellation;
    private Variable? _container;

    public CosmosDbCreateSagaFrame(Variable saga, bool partitionById = false)
    {
        _saga = saga;
        _partitionById = partitionById;
        uses.Add(_saga);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _container = chain.FindVariable(typeof(Container));
        yield return _container;

        if (_partitionById)
        {
            _cancellation = chain.FindVariable(typeof(CancellationToken));
            yield return _cancellation;
        }
    }

    /// <summary>
    ///     A saga that is only ever started never reads a saga id into a local of its own, so the id is read
    ///     back off the saga itself
    /// </summary>
    private string identityExpression(string sagaUsage)
    {
        var member = SagaChain.DetermineSagaIdMember(_saga.VariableType, _saga.VariableType);
        return member == null ? "\"(unknown)\"" : $"{sagaUsage}.{member.Name}";
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // The partitioned write has to go through the JSON itself to stamp the partition key onto the
        // document, so it cannot use the typed CreateItemAsync<T>. See CosmosSagaStorage (GH-3415)
        var create = _partitionById
            ? $"await {typeof(CosmosSagaStorage).FullNameInCode()}.{nameof(CosmosSagaStorage.CreateAsync)}({_container!.Usage}, {_saga.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);"
            : $"await {_container!.Usage}.CreateItemAsync({_saga.Usage}).ConfigureAwait(false);";

        writer.Write("BLOCK:try");
        writer.Write(create);
        writer.FinishBlock();
        CosmosDbConcurrency.WriteConflictCatchBlock(writer, _saga.VariableType, identityExpression(_saga.Usage));

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write("BLOCK:try");

        // CreateItemAsync returns Task<ItemResponse<T>>, not Task; use let! _ = to discard the result.
        // CosmosSagaStorage.CreateAsync returns a plain Task, which the task builder binds with do!
        if (_partitionById)
        {
            writer.Write(
                $"do! {typeof(CosmosSagaStorage).FSharpName()}.{nameof(CosmosSagaStorage.CreateAsync)}({_container!.FSharpUsage}, {_saga.FSharpUsage}, {_cancellation!.FSharpUsage})");
        }
        else
        {
            writer.Write($"let! _ = {_container!.FSharpUsage}.CreateItemAsync({_saga.FSharpUsage})");
            writer.Write("()");
        }

        writer.FinishBlock();
        CosmosDbConcurrency.WriteFSharpConflictCatchBlock(writer, _saga.VariableType,
            identityExpression(_saga.FSharpUsage));

        Next?.GenerateFSharpCode(method, writer);
    }
}

internal class CosmosDbUpsertFrame : AsyncFrame
{
    private readonly Variable _document;
    private readonly bool _optimisticConcurrency;
    private readonly bool _partitionById;
    private Variable? _cancellation;
    private Variable? _container;

    public CosmosDbUpsertFrame(Variable document, bool partitionById = false, bool optimisticConcurrency = false)
    {
        _document = document;
        _partitionById = partitionById;
        _optimisticConcurrency = optimisticConcurrency;
        uses.Add(_document);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _container = chain.FindVariable(typeof(Container));
        yield return _container;

        if (_partitionById)
        {
            _cancellation = chain.FindVariable(typeof(CancellationToken));
            yield return _cancellation;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var etag = _optimisticConcurrency ? LoadDocumentFrame.EtagVariableName(_document) : "null";

        // The partitioned write has to go through the JSON itself to stamp the partition key onto the
        // document, so it cannot use the typed UpsertItemAsync<T>. See CosmosSagaStorage (GH-3415)
        var upsert = _partitionById
            ? $"await {typeof(CosmosSagaStorage).FullNameInCode()}.UpsertAsync({_container!.Usage}, {_document.Usage}, {etag}, {_cancellation!.Usage}).ConfigureAwait(false);"
            : _optimisticConcurrency
                ? $"await {_container!.Usage}.UpsertItemAsync({_document.Usage}, {CosmosDbConcurrency.RequestOptions(etag)}).ConfigureAwait(false);"
                : $"await {_container!.Usage}.UpsertItemAsync({_document.Usage}).ConfigureAwait(false);";

        if (_optimisticConcurrency)
        {
            writer.Write("BLOCK:try");
            writer.Write(upsert);
            writer.FinishBlock();
            CosmosDbConcurrency.WriteCatchBlock(writer, _document.VariableType, SagaChain.SagaIdVariableName);
        }
        else
        {
            writer.Write(upsert);
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var etag = _optimisticConcurrency ? LoadDocumentFrame.EtagVariableName(_document) : "null";

        // UpsertItemAsync returns Task<ItemResponse<T>>, not Task; use let! _ = to discard the result.
        // CosmosSagaStorage.UpsertAsync returns a plain Task, which the task builder binds with do!
        var upsert = _partitionById
            ? [
                $"do! {typeof(CosmosSagaStorage).FSharpName()}.UpsertAsync({_container!.FSharpUsage}, {_document.FSharpUsage}, {etag}, {_cancellation!.FSharpUsage})"
            ]
            : _optimisticConcurrency
                ? new[]
                {
                    $"let! _ = {_container!.FSharpUsage}.UpsertItemAsync({_document.FSharpUsage}, {CosmosDbConcurrency.FSharpRequestOptions(etag)})",
                    "()"
                }
                : new[]
                {
                    $"let! _ = {_container!.FSharpUsage}.UpsertItemAsync({_document.FSharpUsage})",
                    "()"
                };

        if (_optimisticConcurrency)
        {
            writer.Write("BLOCK:try");
            foreach (var line in upsert) writer.Write(line);
            writer.FinishBlock();
            CosmosDbConcurrency.WriteFSharpCatchBlock(writer, _document.VariableType, SagaChain.SagaIdVariableName);
        }
        else
        {
            foreach (var line in upsert) writer.Write(line);
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}

internal class CosmosDbDeleteDocumentFrame : AsyncFrame
{
    private readonly Variable _sagaId;
    private readonly Variable _saga;
    private readonly bool _optimisticConcurrency;
    private readonly bool _partitionById;
    private Variable? _container;

    public CosmosDbDeleteDocumentFrame(Variable sagaId, Variable saga, bool partitionById = false,
        bool optimisticConcurrency = false)
    {
        _sagaId = sagaId;
        _saga = saga;
        _partitionById = partitionById;
        _optimisticConcurrency = optimisticConcurrency;
        uses.Add(_sagaId);
        uses.Add(_saga);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _container = chain.FindVariable(typeof(Container));
        yield return _container;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var partition = LoadDocumentFrame.PartitionKeyExpression(_sagaId, _partitionById);

        if (_optimisticConcurrency)
        {
            // Completing a saga is just as destructive as updating it: a blind delete would drop a
            // concurrent write that landed after this message read the document
            var etag = LoadDocumentFrame.EtagVariableName(_saga);

            writer.Write("BLOCK:try");
            writer.Write(
                $"await {_container!.Usage}.DeleteItemAsync<{_saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {partition}, {CosmosDbConcurrency.RequestOptions(etag)}).ConfigureAwait(false);");
            writer.FinishBlock();
            CosmosDbConcurrency.WriteCatchBlock(writer, _saga.VariableType, _sagaId.Usage);
        }
        else
        {
            writer.Write(
                $"await {_container!.Usage}.DeleteItemAsync<{_saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {partition}).ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var partition = LoadDocumentFrame.FSharpPartitionKeyExpression(_sagaId, _partitionById);

        // DeleteItemAsync returns Task<ItemResponse<T>>, not Task; use let! _ = to discard the result.
        if (_optimisticConcurrency)
        {
            var etag = LoadDocumentFrame.EtagVariableName(_saga);

            writer.Write("BLOCK:try");
            writer.Write(
                $"let! _ = {_container!.FSharpUsage}.DeleteItemAsync<{_saga.VariableType.FSharpName()}>({_sagaId.FSharpUsage}, {partition}, {CosmosDbConcurrency.FSharpRequestOptions(etag)})");
            writer.Write("()");
            writer.FinishBlock();
            CosmosDbConcurrency.WriteFSharpCatchBlock(writer, _saga.VariableType, _sagaId.FSharpUsage);
        }
        else
        {
            writer.Write(
                $"let! _ = {_container!.FSharpUsage}.DeleteItemAsync<{_saga.VariableType.FSharpName()}>({_sagaId.FSharpUsage}, {partition})");
            writer.Write("()");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}

internal class CosmosDbDeleteByVariableFrame : AsyncFrame
{
    private readonly Variable _variable;
    private Variable? _container;

    public CosmosDbDeleteByVariableFrame(Variable variable)
    {
        _variable = variable;
        uses.Add(_variable);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _container = chain.FindVariable(typeof(Container));
        yield return _container;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await {_container!.Usage}.DeleteItemAsync<{_variable.VariableType.FullNameInCode()}>({_variable.Usage}.ToString(), {typeof(PartitionKey).FullNameInCode()}.None).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // DeleteItemAsync returns Task<ItemResponse<T>>, not Task; use let! _ = to discard the result.
        writer.Write(
            $"let! _ = {_container!.FSharpUsage}.DeleteItemAsync<{_variable.VariableType.FSharpName()}>({_variable.FSharpUsage}.ToString(), {typeof(PartitionKey).FSharpName()}.None)");
        writer.Write("()");
        Next?.GenerateFSharpCode(method, writer);
    }
}
