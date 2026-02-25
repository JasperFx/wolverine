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
        return new CosmosDbUpsertFrame(saga);
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        // CosmosDB operations are immediate, but we still flush outgoing messages
        return new FlushOutgoingMessages();
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return new CosmosDbUpsertFrame(saga);
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new CosmosDbDeleteDocumentFrame(sagaId, saga);
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        return DetermineUpdateFrame(saga, container);
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

internal class CosmosDbUpsertFrame : AsyncFrame
{
    private readonly Variable _document;
    private Variable? _container;

    public CosmosDbUpsertFrame(Variable document)
    {
        _document = document;
        uses.Add(_document);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _container = chain.FindVariable(typeof(Container));
        yield return _container;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await {_container!.Usage}.UpsertItemAsync({_document.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

internal class CosmosDbDeleteDocumentFrame : AsyncFrame
{
    private readonly Variable _sagaId;
    private readonly Variable _saga;
    private Variable? _container;

    public CosmosDbDeleteDocumentFrame(Variable sagaId, Variable saga)
    {
        _sagaId = sagaId;
        _saga = saga;
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
        writer.Write(
            $"await {_container!.Usage}.DeleteItemAsync<{_saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {typeof(PartitionKey).FullNameInCode()}.None).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
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
}
