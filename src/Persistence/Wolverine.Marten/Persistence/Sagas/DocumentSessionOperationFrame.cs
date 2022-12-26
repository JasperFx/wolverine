using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;

namespace Wolverine.Marten.Persistence.Sagas;

internal class DocumentSessionOperationFrame : SyncFrame
{
    private readonly string _methodName;
    private readonly Variable _saga;
    private Variable? _session;

    public DocumentSessionOperationFrame(Variable saga, string methodName)
    {
        _saga = saga;
        _methodName = methodName;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_session!.Usage}.{_methodName}({_saga.Usage});");
        Next?.GenerateCode(method, writer);
    }
}