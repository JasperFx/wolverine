using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Fisher;
using Fisher.Events;

namespace Wolverine.Fisher.Codegen;

internal class SessionVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IQuerySession) || type == typeof(IDocumentSession);
    }

    public Variable Create(Type type)
    {
        return new OpenFisherSessionFrame(type).ReturnVariable;
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
    private Variable _session = null!;

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
        return type == typeof(global::Fisher.Events.EventOperations);
    }

    public Variable Create(Type type)
    {
        return new EventOperationsFrame().Variable;
    }
}

internal class EventOperationsFrame : SyncFrame
{
    private Variable _session = null!;

    public EventOperationsFrame()
    {
        Variable = new Variable(typeof(global::Fisher.Events.EventOperations), this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{typeof(global::Fisher.Events.EventOperations)} {Variable.Usage} = {_session.Usage}.{nameof(IDocumentSession.Events)};");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
///     Supplies the <b>shared</b> <see cref="JasperFx.Events.IEventOperations"/> contract rather than
///     Fisher's own derived one, so that Wolverine's store agnostic <c>Storage.AppendEvents()</c> /
///     <c>Storage.StartStream()</c> side effects resolve against any registered event store. The sibling
///     <c>EventOperationsSource</c> above stays as it is because a handler asking for Fisher's own type
///     must keep getting a variable of exactly that type.
/// </summary>
internal class SharedEventOperationsSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(JasperFx.Events.IEventOperations);
    }

    public Variable Create(Type type)
    {
        return new SharedEventOperationsFrame().Variable;
    }
}

internal class SharedEventOperationsFrame : SyncFrame
{
    private Variable _session = null!;

    public SharedEventOperationsFrame()
    {
        Variable = new Variable(typeof(JasperFx.Events.IEventOperations), this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // Deliberately the session rather than a store: an ancillary store's [Storage] frame has already
        // swapped which IDocumentSession this chain resolves, so this follows it for free.
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(JasperFx.Events.IEventOperations)} {Variable.Usage} = {_session.Usage}.{nameof(IDocumentSession.Events)};");
        Next?.GenerateCode(method, writer);
    }
}
