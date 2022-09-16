using System.Collections.Generic;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Marten;

namespace Wolverine.Persistence.Marten.Persistence.Sagas;

internal class DocumentSessionOperationFrame : SyncFrame
{
    private readonly Variable _saga;
    private readonly string _methodName;
    private Variable _session;

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
        writer.Write($"{_session.Usage}.{_methodName}({_saga.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
