using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Polecat;
using Polecat.Events;

namespace Wolverine.Polecat.Codegen;

internal class SessionVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IQuerySession) || type == typeof(IDocumentSession);
    }

    public Variable Create(Type type)
    {
        return new OpenPolecatSessionFrame(type).ReturnVariable;
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

internal class EventOperationsSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IEventOperations);
    }

    public Variable Create(Type type)
    {
        return new EventOperationsFrame().Variable;
    }
}

internal class EventOperationsFrame : SyncFrame
{
    private Variable _session;

    public EventOperationsFrame()
    {
        Variable = new Variable(typeof(IEventOperations), this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{typeof(IEventOperations)} {Variable.Usage} = {_session.Usage}.{nameof(IDocumentSession.Events)};");
        Next?.GenerateCode(method, writer);
    }
}
