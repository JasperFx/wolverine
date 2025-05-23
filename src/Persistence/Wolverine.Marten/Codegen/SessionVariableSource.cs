using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;

namespace Wolverine.Marten.Codegen;

internal class SessionVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IQuerySession) || type == typeof(IDocumentSession);
    }

    public Variable Create(Type type)
    {
        return new OpenMartenSessionFrame(type).ReturnVariable;
    }
}

internal class DocumentOperationsSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IDocumentOperations);
    }

    public Variable Create(Type type)
    {
        return new DocumentOperationsFrame().Variable;
    }
}

internal class DocumentOperationsFrame : SyncFrame
{
    private Variable _session;

    public DocumentOperationsFrame()
    {
        Variable = new Variable(typeof(IDocumentOperations), this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{typeof(IDocumentOperations)} {Variable.Usage} = {_session.Usage};");
        Next?.GenerateCode(method, writer);
    }
}