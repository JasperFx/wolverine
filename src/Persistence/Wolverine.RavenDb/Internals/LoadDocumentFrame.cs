using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Raven.Client.Documents.Session;

namespace Wolverine.RavenDb.Internals;

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
        _session = chain.FindVariable(typeof(IAsyncDocumentSession));
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
        
        Next?.GenerateCode(method, writer);
    }
}