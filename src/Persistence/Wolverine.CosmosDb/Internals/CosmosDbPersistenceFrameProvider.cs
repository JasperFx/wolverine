using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Azure.Cosmos;
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

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        return new LoadDocumentFrame(sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        // A brand new document has no ETag to match against, so there is nothing to be optimistic about
        return new CosmosDbUpsertFrame(saga);
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        // CosmosDB operations are immediate, but we still flush outgoing messages
        return new FlushOutgoingMessages();
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        // Updating a saga document is a compare-and-swap against the ETag captured by LoadDocumentFrame
        // when the saga was read. Without it, two messages for the same saga id racing on separate nodes
        // both upsert blindly and the second one silently overwrites the first. See GH-3414.
        return new CosmosDbUpsertFrame(saga, LoadDocumentFrame.UsesOptimisticConcurrency(saga));
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new CosmosDbDeleteDocumentFrame(sagaId, saga, LoadDocumentFrame.UsesOptimisticConcurrency(saga));
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        // Storage.Store() is an explicit "just write it" side effect on an arbitrary document, not the
        // saga update path, so it deliberately stays a blind upsert
        return new CosmosDbUpsertFrame(saga);
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return new CosmosDbDeleteByVariableFrame(variable);
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var method = typeof(CosmosDbStorageActionApplier).GetMethod("ApplyAction")!
            .MakeGenericMethod(entityType);

        var call = new MethodCall(typeof(CosmosDbStorageActionApplier), method);
        call.Arguments[1] = action;

        return call;
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
}

/// <summary>
///     Emits the compare-and-swap plumbing that turns a 412 Precondition Failed from CosmosDB into the
///     <see cref="SagaConcurrencyException" /> Wolverine's saga retry machinery already understands. Shared
///     by the saga upsert and delete frames so both report the violation identically.
/// </summary>
internal static class CosmosDbConcurrency
{
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
        writer.Write(
            $"BLOCK:catch ({typeof(CosmosException).FullNameInCode()} e) when (e.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)");
        writer.WriteComment("The saga document was changed by another message since it was read");
        writer.Write(
            $"throw new {typeof(SagaConcurrencyException).FullNameInCode()}($\"Saga of type {sagaType.FullNameInCode()} and id {{{identityExpression}}} cannot be updated because of optimistic concurrency violations\", e);");
        writer.FinishBlock();
    }

    public static void WriteFSharpCatchBlock(ISourceWriter writer, Type sagaType, string identityExpression)
    {
        writer.Write(
            $"BLOCK:with :? {typeof(CosmosException).FSharpName()} as e when e.StatusCode = System.Net.HttpStatusCode.PreconditionFailed ->");
        writer.WriteComment("The saga document was changed by another message since it was read");
        writer.Write(
            $"raise ({typeof(SagaConcurrencyException).FSharpName()}(sprintf \"Saga of type {sagaType.FullNameInCode()} and id %O cannot be updated because of optimistic concurrency violations\" {identityExpression}, e))");
        writer.FinishBlock();
    }
}

internal class CosmosDbUpsertFrame : AsyncFrame
{
    private readonly Variable _document;
    private readonly bool _optimisticConcurrency;
    private Variable? _container;

    public CosmosDbUpsertFrame(Variable document, bool optimisticConcurrency = false)
    {
        _document = document;
        _optimisticConcurrency = optimisticConcurrency;
        uses.Add(_document);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _container = chain.FindVariable(typeof(Container));
        yield return _container;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_optimisticConcurrency)
        {
            var etag = LoadDocumentFrame.EtagVariableName(_document);

            writer.Write("BLOCK:try");
            writer.Write(
                $"await {_container!.Usage}.UpsertItemAsync({_document.Usage}, {CosmosDbConcurrency.RequestOptions(etag)}).ConfigureAwait(false);");
            writer.FinishBlock();
            CosmosDbConcurrency.WriteCatchBlock(writer, _document.VariableType, SagaChain.SagaIdVariableName);
        }
        else
        {
            writer.Write(
                $"await {_container!.Usage}.UpsertItemAsync({_document.Usage}).ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // UpsertItemAsync returns Task<ItemResponse<T>>, not Task; use let! _ = to discard the result.
        if (_optimisticConcurrency)
        {
            var etag = LoadDocumentFrame.EtagVariableName(_document);

            writer.Write("BLOCK:try");
            writer.Write(
                $"let! _ = {_container!.FSharpUsage}.UpsertItemAsync({_document.FSharpUsage}, {CosmosDbConcurrency.FSharpRequestOptions(etag)})");
            writer.Write("()");
            writer.FinishBlock();
            CosmosDbConcurrency.WriteFSharpCatchBlock(writer, _document.VariableType, SagaChain.SagaIdVariableName);
        }
        else
        {
            writer.Write($"let! _ = {_container!.FSharpUsage}.UpsertItemAsync({_document.FSharpUsage})");
            writer.Write("()");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}

internal class CosmosDbDeleteDocumentFrame : AsyncFrame
{
    private readonly Variable _sagaId;
    private readonly Variable _saga;
    private readonly bool _optimisticConcurrency;
    private Variable? _container;

    public CosmosDbDeleteDocumentFrame(Variable sagaId, Variable saga, bool optimisticConcurrency = false)
    {
        _sagaId = sagaId;
        _saga = saga;
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
        if (_optimisticConcurrency)
        {
            // Completing a saga is just as destructive as updating it: a blind delete would drop a
            // concurrent write that landed after this message read the document
            var etag = LoadDocumentFrame.EtagVariableName(_saga);

            writer.Write("BLOCK:try");
            writer.Write(
                $"await {_container!.Usage}.DeleteItemAsync<{_saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {typeof(PartitionKey).FullNameInCode()}.None, {CosmosDbConcurrency.RequestOptions(etag)}).ConfigureAwait(false);");
            writer.FinishBlock();
            CosmosDbConcurrency.WriteCatchBlock(writer, _saga.VariableType, _sagaId.Usage);
        }
        else
        {
            writer.Write(
                $"await {_container!.Usage}.DeleteItemAsync<{_saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {typeof(PartitionKey).FullNameInCode()}.None).ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // DeleteItemAsync returns Task<ItemResponse<T>>, not Task; use let! _ = to discard the result.
        if (_optimisticConcurrency)
        {
            var etag = LoadDocumentFrame.EtagVariableName(_saga);

            writer.Write("BLOCK:try");
            writer.Write(
                $"let! _ = {_container!.FSharpUsage}.DeleteItemAsync<{_saga.VariableType.FSharpName()}>({_sagaId.FSharpUsage}, {typeof(PartitionKey).FSharpName()}.None, {CosmosDbConcurrency.FSharpRequestOptions(etag)})");
            writer.Write("()");
            writer.FinishBlock();
            CosmosDbConcurrency.WriteFSharpCatchBlock(writer, _saga.VariableType, _sagaId.FSharpUsage);
        }
        else
        {
            writer.Write(
                $"let! _ = {_container!.FSharpUsage}.DeleteItemAsync<{_saga.VariableType.FSharpName()}>({_sagaId.FSharpUsage}, {typeof(PartitionKey).FSharpName()}.None)");
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
