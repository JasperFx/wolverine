using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Metadata;

namespace Wolverine.Marten.Persistence.Sagas;

internal class LoadDocumentFrame : AsyncFrame
{
    public const string ExpectedSagaRevision = "expectedSagaRevision";
    
    private readonly Variable _sagaId;
    private Variable? _cancellation;
    private Variable? _session;

    public LoadDocumentFrame(Type sagaType, Variable sagaId)
    {
        _sagaId = sagaId;

        Saga = new Variable(sagaType, this);
    }


    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Try to load the existing saga document");
        writer.Write(
            $"var {Saga.Usage} = await {_session!.Usage}.LoadAsync<{Saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        if (Saga.VariableType.CanBeCastTo<IRevisioned>())
        {
            writer.WriteComment($"{Saga.VariableType.FullNameInCode()} implements {typeof(IRevisioned).FullNameInCode()}, so Wolverine will try to update based on the revision as a concurrency protection");
            writer.WriteLine($"var {ExpectedSagaRevision} = {Saga.Usage}.{nameof(IRevisioned.Version)} + 1;");
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