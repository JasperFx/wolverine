using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Metadata;
using Wolverine.Marten.Codegen;

namespace Wolverine.Marten.Persistence.Sagas;

internal class LoadDocumentFrame : AsyncFrame, IBatchableFrame
{
    public const string ExpectedSagaRevision = "expectedSagaRevision";
    
    private readonly Variable _sagaId;
    private Variable? _cancellation;
    private Variable? _session;
    private Variable? _batchQuery;
    private Variable? _batchQueryItem;

    public LoadDocumentFrame(Type sagaType, Variable sagaId)
    {
        _sagaId = sagaId;
        uses.Add(sagaId);

        Saga = new Variable(sagaType, this);
    }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null)
            throw new InvalidOperationException("This frame has not been enlisted in a MartenBatchFrame");
        
        writer.Write(
            $"var {_batchQueryItem.Usage} = {_batchQuery!.Usage}.Load<{Saga.VariableType.FullNameInCode()}>({_sagaId.Usage});");
    }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQuery = batchQuery;
        _batchQueryItem = new Variable(typeof(Task<>).MakeGenericType(Saga.VariableType), Saga.Usage + "_BatchItem",
            this);
    }


    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Try to load the existing saga document");
        if (_batchQueryItem == null)
        {
            writer.Write(
                $"var {Saga.Usage} = await {_session!.Usage}.LoadAsync<{Saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        }
        else
        {
            writer.Write(
                $"var {Saga.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");
        }
        
        if (Saga.VariableType.CanBeCastTo<IRevisioned>() && Saga.VariableType.CanBeCastTo<Saga>())
        {
            writer.WriteComment($"{Saga.VariableType.FullNameInCode()} implements {typeof(IRevisioned).FullNameInCode()}, so Wolverine will try to update based on the revision as a concurrency protection");
            writer.Write($"var {ExpectedSagaRevision} = 0;");
            writer.Write($"BLOCK:if ({Saga.Usage} != null)");
            writer.Write($"{ExpectedSagaRevision} = {Saga.Usage}.{nameof(IRevisioned.Version)} + 1;");
            writer.FinishBlock();
        }
        
        Next?.GenerateCode(method, writer);
    }
}

internal class UpdateSagaRevisionFrame : SyncFrame
{
    private Variable _session;
    public Variable Saga { get; }

    public UpdateSagaRevisionFrame(Variable saga)
    {
        Saga = saga;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Register the document operation with the current session against the expected *final* revision");
        writer.Write($"{_session!.Usage}.{nameof(IDocumentOperations.UpdateRevision)}({Saga.Usage}, {LoadDocumentFrame.ExpectedSagaRevision});");
        Next?.GenerateCode(method, writer);
    }
}