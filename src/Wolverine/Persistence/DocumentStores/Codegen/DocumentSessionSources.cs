using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.DocumentStores.Codegen;

/// <summary>
///     GH-5141: the types a document store integration (Marten, Polecat, Fisher) hands to Wolverine core's
///     codegen sources, so the sources and frames are written once and differ only by these.
/// </summary>
internal sealed class DocumentSessionTypes
{
    /// <summary>
    ///     The store's name, for generated comments ("Building the Marten session")
    /// </summary>
    public required string StoreName { get; init; }

    /// <summary>
    ///     The store's own read/write session interface, e.g. <c>Marten.IDocumentSession</c>
    /// </summary>
    public required Type DocumentSession { get; init; }

    /// <summary>
    ///     The store's own read-only session interface, e.g. <c>Marten.IQuerySession</c>
    /// </summary>
    public required Type QuerySession { get; init; }

    /// <summary>
    ///     The integration's outbox-enrolling session factory, which has <c>OpenSession(MessageContext[, tenantId])</c>
    ///     and <c>QuerySession(MessageContext[, tenantId])</c>
    /// </summary>
    public required Type OutboxedSessionFactory { get; init; }

    /// <summary>
    ///     The store's own session factory, used when the chain has no Wolverine message context at all
    /// </summary>
    public required Type NativeSessionFactory { get; init; }

    /// <summary>
    ///     The store's own document operations contract, e.g. <c>Marten.IDocumentOperations</c>
    /// </summary>
    public required Type DocumentOperations { get; init; }

    /// <summary>
    ///     The store's own type for <c>session.Events</c>, as a handler may ask for it
    ///     (<c>Marten.Events.IEventStoreOperations</c>, <c>Polecat.Events.IEventOperations</c>,
    ///     <c>Fisher.Events.EventOperations</c>)
    /// </summary>
    public required Type EventOperations { get; init; }
}

/// <summary>
///     Supplies a store's <see cref="DocumentSessionTypes.DocumentSession" /> and
///     <see cref="DocumentSessionTypes.QuerySession" /> by opening an outbox-enrolled session.
/// </summary>
internal class DocumentSessionVariableSource : IVariableSource
{
    private readonly DocumentSessionTypes _types;

    public DocumentSessionVariableSource(DocumentSessionTypes types)
    {
        _types = types;
    }

    public bool Matches(Type type)
    {
        return type == _types.QuerySession || type == _types.DocumentSession;
    }

    public Variable Create(Type type)
    {
        return new OpenDocumentSessionFrame(_types, type).ReturnVariable;
    }
}

/// <summary>
///     Opens a store session for a chain: through the integration's outbox-enrolling session factory when the chain
///     has a Wolverine message context (with the detected tenant id when there is one), through the store's own
///     session factory otherwise. A query session simply reuses the chain's document session when there is one.
/// </summary>
internal class OpenDocumentSessionFrame : AsyncFrame
{
    // The method names on every integration's OutboxedSessionFactory
    private const string OpenSessionMethod = "OpenSession";
    private const string QuerySessionMethod = "QuerySession";

    private readonly DocumentSessionTypes _types;
    private readonly Type _sessionType;
    private Variable? _context;
    private Variable? _factory;
    private Variable? _nativeFactory;
    private Variable _tenantId = null!;
    private bool _justCast;

    public OpenDocumentSessionFrame(DocumentSessionTypes types, Type sessionType)
    {
        _types = types;
        _sessionType = sessionType;
        ReturnVariable = new Variable(sessionType, this);
    }

    public Variable ReturnVariable { get; }

    private string methodName => ReturnVariable.VariableType == _types.QuerySession
        ? QuerySessionMethod
        : OpenSessionMethod;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_justCast)
        {
            Next?.GenerateCode(method, writer);
            return;
        }

        if (_context == null)
        {
            // Just use the store's own session factory here.
            writer.Write($"await using var {ReturnVariable.Usage} = {_nativeFactory!.Usage}.{methodName}();");
        }
        else if (_tenantId == null)
        {
            writer.WriteComment($"Building the {_types.StoreName} session");
            writer.Write($"await using var {ReturnVariable.Usage} = {_factory!.Usage}.{methodName}({_context!.Usage});");
        }
        else
        {
            writer.WriteComment($"Building the {_types.StoreName} session using the detected tenant id");
            writer.Write($"await using var {ReturnVariable.Usage} = {_factory!.Usage}.{methodName}({_context!.Usage}, {_tenantId.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_justCast)
        {
            Next?.GenerateFSharpCode(method, writer);
            return;
        }

        // `await using var` -> F# `use`: inside the task { } computation expression `use` disposes an
        // IAsyncDisposable (the session) via DisposeAsync at the end of the block.
        if (_context == null)
        {
            writer.Write($"use {ReturnVariable.Usage} = {_nativeFactory!.FSharpUsage}.{methodName}()");
        }
        else if (_tenantId == null)
        {
            writer.WriteComment($"Building the {_types.StoreName} session");
            writer.Write($"use {ReturnVariable.Usage} = {_factory!.FSharpUsage}.{methodName}({_context!.FSharpUsage})");
        }
        else
        {
            writer.WriteComment($"Building the {_types.StoreName} session using the detected tenant id");
            writer.Write($"use {ReturnVariable.Usage} = {_factory!.FSharpUsage}.{methodName}({_context!.FSharpUsage}, {_tenantId.FSharpUsage})");
        }

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        if (_sessionType == _types.QuerySession)
        {
            _justCast = true;
            var documentSession = chain.TryFindVariable(_types.DocumentSession, VariableSource.All);
            if (documentSession != null)
            {
                yield return documentSession;
                ReturnVariable.OverrideName($"(({_types.QuerySession}){documentSession.Usage})");
                yield break;
            }
        }

        // Honestly, this is mostly to get the ordering correct
        if (chain.TryFindVariableByName(typeof(string), PersistenceConstants.TenantIdVariableName, out var tenant))
        {
            _tenantId = tenant;
            yield return _tenantId;

            // Mandatory in this case
            _context = chain.FindVariable(typeof(MessageContext));
        }
        else
        {
            // Do a Try/Find here
            _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices)
                       ?? chain.TryFindVariable(typeof(IMessageBus), VariableSource.NotServices);
        }

        if (_context != null)
        {
            yield return _context;
            _factory = chain.FindVariable(_types.OutboxedSessionFactory);
            yield return _factory;
        }
        else
        {
            _nativeFactory = chain.FindVariable(_types.NativeSessionFactory);
            yield return _nativeFactory;
        }
    }
}

/// <summary>
///     Supplies every contract a store's document session can be handed out as, from the chain's single document
///     session: the store's own <see cref="DocumentSessionTypes.DocumentOperations" /> and
///     <see cref="DocumentSessionTypes.EventOperations" />, and the store-agnostic JasperFx contracts
///     (<c>IEventOperations</c>, <c>IEventStoreOperations</c>, <c>IDocumentSessionOperations</c>,
///     <c>IDocumentWriteOperations</c>, <c>IDocumentReadOperations</c>).
/// </summary>
/// <remarks>
///     <para>
///         GH-5141: one source for what were five per store (<c>DocumentOperationsSource</c>,
///         <c>EventOperationsSource</c> / <c>EventStoreOperationsSource</c>, <c>SharedEventOperationsSource</c>,
///         <c>SharedEventStoreOperationsSource</c>, <c>SharedDocumentOperationsSource</c>).
///     </para>
///     <para>
///         The store-agnostic contracts (GH-3956) let a handler or HTTP endpoint written against the abstraction
///         stay valid on Marten, Polecat and Fisher alike. All of them — including the read-only contract —
///         deliberately resolve from the chain's single document session rather than opening a query session for
///         the read side. A handler that takes <c>IDocumentReadOperations</c> alongside
///         <c>IDocumentWriteOperations</c> would otherwise get two different sessions, and its reads would not see
///         its own pending writes. Going through the session (not a store) also means an ancillary store's
///         <c>[Storage]</c> frame has already swapped which session this resolves, and these follow it for free.
///     </para>
///     <para>
///         Known limitation, unchanged by GH-5141: every integration registers a source for the store-agnostic
///         contracts, and the first registered wins, whichever store the chain targets.
///     </para>
/// </remarks>
internal class DocumentSessionContractSource : IVariableSource
{
    private static readonly Type[] _sharedSessionContracts =
    [
        typeof(JasperFx.Events.Documents.IDocumentSessionOperations),
        typeof(JasperFx.Events.Documents.IDocumentWriteOperations),
        typeof(JasperFx.Events.Documents.IDocumentReadOperations)
    ];

    private static readonly Type[] _sharedEventContracts =
    [
        typeof(JasperFx.Events.IEventOperations),
        typeof(JasperFx.Events.IEventStoreOperations)
    ];

    private readonly DocumentSessionTypes _types;

    public DocumentSessionContractSource(DocumentSessionTypes types)
    {
        _types = types;
    }

    public bool Matches(Type type)
    {
        return type == _types.DocumentOperations
               || type == _types.EventOperations
               || _sharedSessionContracts.Contains(type)
               || _sharedEventContracts.Contains(type);
    }

    public Variable Create(Type type)
    {
        var viaEvents = type == _types.EventOperations || _sharedEventContracts.Contains(type);
        return new DocumentSessionContractFrame(type, _types.DocumentSession, viaEvents).Variable;
    }
}

/// <summary>
///     Hands out the chain's document session (or its <c>Events</c>) as one of the contracts it implements.
/// </summary>
internal class DocumentSessionContractFrame : SyncFrame
{
    private const string EventsMember = "Events";

    private readonly Type _contractType;
    private readonly Type _sessionType;
    private readonly bool _viaEvents;
    private Variable _session = null!;

    public DocumentSessionContractFrame(Type contractType, Type sessionType, bool viaEvents)
    {
        _contractType = contractType;
        _sessionType = sessionType;
        _viaEvents = viaEvents;
        Variable = new Variable(contractType, this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(_sessionType);
        yield return _session;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Reads the Events property of a store session interface for F# codegen; codegen-time only.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Reads the Events property of a store session interface for F# codegen; codegen-time only.")]
    [UnconditionalSuppressMessage("Trimming", "IL2080",
        Justification = "Reads the Events property of a store session interface for F# codegen; codegen-time only.")]
    private Type? staticSourceType()
    {
        if (!_viaEvents) return _sessionType;

        // Session types are interfaces, whose inherited members GetProperty does not see
        return new[] { _sessionType }.Concat(_sessionType.GetInterfaces())
            .Select(x => x.GetProperty(EventsMember)?.PropertyType)
            .FirstOrDefault(x => x != null);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // A plain assignment -- the session (or its Events) implements every one of these contracts, so no cast.
        var source = _viaEvents ? $"{_session.Usage}.{EventsMember}" : _session.Usage;
        writer.Write($"{_contractType.FullNameInCode()} {Variable.Usage} = {source};");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // F# does not upcast to an interface implicitly in a let binding, so say so -- unless the session's
        // Events member already is the contract type, where F# would warn that the upcast is unnecessary
        var source = _viaEvents ? $"{_session.FSharpUsage}.{EventsMember}" : _session.FSharpUsage;
        writer.Write(staticSourceType() == _contractType
            ? $"let {Variable.Usage} = {source}"
            : $"let {Variable.Usage} = {source} :> {_contractType.FSharpName()}");
        Next?.GenerateFSharpCode(method, writer);
    }
}
