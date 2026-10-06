using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.InMemory.Codegen;

/// <summary>
/// Opens the session for a chain: <see cref="IInMemoryDocumentSession"/>, or a read-only
/// <see cref="IInMemoryQuerySession"/> that is just the writable session when the chain already has one.
/// </summary>
internal class OpenInMemorySessionFrame : AsyncFrame
{
    private readonly Type _sessionType;
    private Variable? _context;
    private Variable _factory = null!;
    private Variable? _tenantId;
    private bool _justCast;

    public OpenInMemorySessionFrame(Type sessionType)
    {
        _sessionType = sessionType;
        ReturnVariable = new Variable(sessionType, this);
    }

    public Variable ReturnVariable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        if (_sessionType == typeof(IInMemoryQuerySession))
        {
            var documentSession = chain.TryFindVariable(typeof(IInMemoryDocumentSession), VariableSource.All);
            if (documentSession != null)
            {
                _justCast = true;
                yield return documentSession;
                ReturnVariable.OverrideName($"(({typeof(IInMemoryQuerySession).FullNameInCode()}){documentSession.Usage})");
                yield break;
            }
        }

        if (chain.TryFindVariableByName(typeof(string), PersistenceConstants.TenantIdVariableName, out var tenant))
        {
            _tenantId = tenant;
            yield return _tenantId;
        }

        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices);
        if (_context != null) yield return _context;

        _factory = chain.FindVariable(typeof(InMemorySessionFactory));
        yield return _factory;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (!_justCast)
        {
            var methodName = _sessionType == typeof(IInMemoryQuerySession)
                ? nameof(InMemorySessionFactory.QuerySession)
                : nameof(InMemorySessionFactory.OpenSession);

            var context = _context?.Usage ?? "null";
            var arguments = _tenantId == null ? context : $"{context}, {_tenantId.Usage}";

            writer.WriteComment("Opening a session on the in-memory prototyping store");
            writer.Write($"await using var {ReturnVariable.Usage} = {_factory.Usage}.{methodName}({arguments});");
        }

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// The transactional middleware's first frame: makes sure the chain's session is opened before the
/// handler, and runs an eager idempotency check when the chain asks for one.
/// </summary>
internal class CreateInMemorySessionFrame : AsyncFrame
{
    private readonly IChain _chain;
    private Variable? _cancellation;
    private Variable? _context;

    public CreateInMemorySessionFrame(IChain chain)
    {
        _chain = chain;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return chain.FindVariable(typeof(IInMemoryDocumentSession));

        if (_chain.Idempotency != IdempotencyStyle.None)
        {
            _cancellation = chain.FindVariable(typeof(CancellationToken));
            yield return _cancellation;

            _context = chain.FindVariable(typeof(MessageContext));
            yield return _context;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_context != null)
        {
            writer.WriteComment("This message handler is configured for Eager idempotency checks");
            writer.Write($"await {_context.Usage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation!.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>Commits the chain's in-memory session: documents and events, all or nothing.</summary>
internal class InMemorySessionSaveChanges : MethodCall
{
    // Targeted at the store's own session type, so the call resolves the chain's one session
    public InMemorySessionSaveChanges() : base(typeof(IInMemoryDocumentSession),
        ReflectionHelper.GetMethod<IDocumentSessionOperations>(x => x.SaveChangesAsync(default))!)
    {
        CommentText = "Commit the in-memory session's unit of work";
    }
}

internal class InMemorySessionSource : IVariableSource
{
    public bool Matches(Type type) => type == typeof(IInMemoryDocumentSession) || type == typeof(IInMemoryQuerySession);

    public Variable Create(Type type) => new OpenInMemorySessionFrame(type).ReturnVariable;
}

/// <summary>
/// The store-agnostic contract types a handler may take -- the session's document and event APIs -- all
/// resolved off the chain's one <see cref="IInMemoryDocumentSession"/>.
/// </summary>
internal class SessionContractSource : IVariableSource
{
    private static readonly Type[] DocumentContracts =
    [
        typeof(IDocumentSessionOperations), typeof(IDocumentWriteOperations), typeof(IDocumentReadOperations)
    ];

    private static readonly Type[] EventContracts = [typeof(IEventStoreOperations), typeof(IEventOperations)];

    public bool Matches(Type type) => DocumentContracts.Contains(type) || EventContracts.Contains(type);

    public Variable Create(Type type)
        => new SessionContractFrame(type, EventContracts.Contains(type)).Variable;
}

internal class SessionContractFrame : SyncFrame
{
    private readonly Type _contractType;
    private readonly bool _isEvents;
    private Variable _session = null!;

    public SessionContractFrame(Type contractType, bool isEvents)
    {
        _contractType = contractType;
        _isEvents = isEvents;
        Variable = new Variable(contractType, this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IInMemoryDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // IDocumentSessionOperations.Events is the event store API; IEventOperations is a base of it
        var source = _isEvents
            ? $"(({typeof(IDocumentSessionOperations).FullNameInCode()}){_session.Usage}).{nameof(IDocumentSessionOperations.Events)}"
            : _session.Usage;

        writer.Write($"{_contractType.FullNameInCode()} {Variable.Usage} = {source};");
        Next?.GenerateCode(method, writer);
    }
}
