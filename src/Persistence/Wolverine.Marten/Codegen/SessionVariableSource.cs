using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Marten.Events;
// JasperFx.Events deliberately excluded — IEventStoreOperations is ambiguous
// between Marten.Events (derived) and JasperFx.Events (lifted base). Pick the
// Marten side; the Marten interface inherits the lifted contract anyway.

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

internal class EventStoreOperationsSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IEventStoreOperations);
    }

    public Variable Create(Type type)
    {
        return new EventStoreOperationsFrame().Variable;
    }
}


internal class EventStoreOperationsFrame : SyncFrame
{
    private Variable _session = null!;

    public EventStoreOperationsFrame()
    {
        Variable = new Variable(typeof(IEventStoreOperations), this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{typeof(IEventStoreOperations)} {Variable.Usage} = {_session.Usage}.{nameof(IDocumentSession.Events)};");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
///     Supplies the <b>shared</b> <see cref="JasperFx.Events.IEventOperations"/> contract rather than
///     Marten's own derived one, so that Wolverine's store agnostic <c>Storage.AppendEvents()</c> /
///     <c>Storage.StartStream()</c> side effects resolve against any registered event store. The sibling
///     <c>EventOperationsSource</c> above stays as it is because a handler asking for Marten's own type
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

/// <summary>
///     Supplies the <b>shared</b> <see cref="JasperFx.Events.IEventStoreOperations"/> contract — the full
///     read + write session-level event API — rather than Marten's own derived spelling, so a message handler
///     or HTTP endpoint can take it as a parameter and stay valid on Marten, Polecat and Fisher alike.
/// </summary>
/// <remarks>
///     Sibling of <see cref="SharedEventOperationsSource"/>, which supplies the narrower write-only
///     <c>IEventOperations</c>. Both resolve to exactly the same thing — <c>session.Events</c> — and both go
///     through <c>IDocumentSession</c> rather than a store, so an ancillary store's <c>[Storage]</c> frame has
///     already swapped the session and these follow it.
/// </remarks>
internal class SharedEventStoreOperationsSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(JasperFx.Events.IEventStoreOperations);
    }

    public Variable Create(Type type)
    {
        return new SharedEventStoreOperationsFrame().Variable;
    }
}

internal class SharedEventStoreOperationsFrame : SyncFrame
{
    private Variable _session = null!;

    public SharedEventStoreOperationsFrame()
    {
        Variable = new Variable(typeof(JasperFx.Events.IEventStoreOperations), this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(JasperFx.Events.IEventStoreOperations)} {Variable.Usage} = {_session.Usage}.{nameof(IDocumentSession.Events)};");
        Next?.GenerateCode(method, writer);
    }
}
