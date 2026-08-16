using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
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

/// <summary>
///     Supplies the <b>shared</b> <see cref="JasperFx.Events.IEventStoreOperations"/> contract — the full
///     read + write session-level event API — rather than Fisher's own derived spelling, so a message handler
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

/// <summary>
///     GH-3956. Supplies the store-agnostic <c>JasperFx.Events.Documents</c> document contracts — the
///     document-side counterparts to <see cref="SharedEventOperationsSource"/> — so that a handler or HTTP
///     endpoint written against the abstraction stays valid on Marten, Polecat and Fisher alike.
/// </summary>
/// <remarks>
///     <para>
///         Fisher's own <c>IDocumentSession</c> already implements all three, so a handler declaring one of
///         these bound and ran before this existed. What it did not get was a commit: codegen matches a
///         variable by its exact type, so nothing satisfied the parameter from the session the chain had
///         created, and <c>CanApply</c> did not recognise the abstraction either.
///     </para>
///     <para>
///         All three — including the read-only contract — deliberately resolve from the chain's single
///         <c>IDocumentSession</c> rather than opening a <c>IQuerySession</c> for the read side. A handler
///         that takes <c>IDocumentReadOperations</c> alongside <c>IDocumentWriteOperations</c> would
///         otherwise get two different sessions, and its reads would not see its own pending writes.
///     </para>
/// </remarks>
internal class SharedDocumentOperationsSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(JasperFx.Events.Documents.IDocumentSessionOperations)
               || type == typeof(JasperFx.Events.Documents.IDocumentWriteOperations)
               || type == typeof(JasperFx.Events.Documents.IDocumentReadOperations);
    }

    public Variable Create(Type type)
    {
        return new SharedDocumentOperationsFrame(type).Variable;
    }
}

internal class SharedDocumentOperationsFrame : SyncFrame
{
    private readonly Type _contractType;
    private Variable _session = null!;

    public SharedDocumentOperationsFrame(Type contractType)
    {
        _contractType = contractType;
        Variable = new Variable(contractType, this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // A plain assignment -- IDocumentSession derives from every one of these contracts, so no cast.
        writer.Write($"{_contractType.FullNameInCode()} {Variable.Usage} = {_session.Usage};");
        Next?.GenerateCode(method, writer);
    }
}
