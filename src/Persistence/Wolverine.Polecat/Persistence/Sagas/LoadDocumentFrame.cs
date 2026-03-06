using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;

namespace Wolverine.Polecat.Persistence.Sagas;

internal class LoadDocumentFrame : AsyncFrame
{
    private readonly Variable _sagaId;
    private Variable? _cancellation;
    private Variable? _session;

    public LoadDocumentFrame(Type sagaType, Variable sagaId)
    {
        _sagaId = sagaId;
        uses.Add(sagaId);

        var usage = $"{Variable.DefaultArgName(sagaType)}_{sagaId.Usage.Split('.').Last()}";
        Saga = new Variable(sagaType, usage, this);
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _sagaId;

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

        Next?.GenerateCode(method, writer);
    }
}
