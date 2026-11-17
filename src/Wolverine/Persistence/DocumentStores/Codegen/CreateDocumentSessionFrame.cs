using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.DocumentStores.Codegen;

/// <summary>
///     Transactional middleware for a document store chain: opens an outbox-enrolled session when the chain does
///     not have one yet, and runs the eager idempotency check when the chain asks for it.
/// </summary>
/// <remarks>
///     GH-5141: written once for Marten, Polecat and Fisher, with the F# rendering only Marten's copy had. Each
///     integration still derives its own type from this one, because its persistence frame provider asks
///     "has this chain already got <b>my</b> session middleware?" with <c>Middleware.OfType&lt;T&gt;()</c>, and
///     a chain that writes to two stores must not mistake one store's frame for the other's.
/// </remarks>
internal abstract class CreateDocumentSessionFrame : Frame
{
    // The method name on every integration's OutboxedSessionFactory
    private const string OpenSessionMethod = "OpenSession";

    private readonly IChain _chain;
    private readonly DocumentSessionTypes _types;
    private Variable? _cancellation;
    private Variable? _context;
    private bool _createsSession;
    private Variable? _factory;

    protected CreateDocumentSessionFrame(IChain chain, DocumentSessionTypes types) : base(true)
    {
        _chain = chain;
        _types = types;
    }

    public Variable? Session { get; private set; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        Session = chain.TryFindVariable(_types.DocumentSession, VariableSource.NotServices);
        if (Session == null)
        {
            _createsSession = true;
            Session = new Variable(_types.DocumentSession, this);

            _factory = chain.FindVariable(_types.OutboxedSessionFactory);
            yield return _factory;
        }

        // Inside of messaging. Not sure how this is gonna work for HTTP yet
        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices);

        if (_context != null)
        {
            yield return _context;
        }

        if (Session != null)
        {
            yield return Session;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_createsSession)
        {
            writer.BlankLine();
            writer.WriteComment("Open a new document session registered with the Wolverine");
            writer.WriteComment("message context to support the outbox functionality");
            writer.Write(
                $"using var {Session!.Usage} = {_factory!.Usage}.{OpenSessionMethod}({_context!.Usage});");
        }

        if (_chain.Idempotency != IdempotencyStyle.None)
        {
            writer.BlankLine();
            writer.WriteComment("This message handler is configured for Eager idempotency checks");

            writer.Write($"await {_context!.Usage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation!.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_createsSession)
        {
            writer.BlankLine();
            writer.WriteComment("Open a new document session registered with the Wolverine");
            writer.WriteComment("message context to support the outbox functionality");
            // `using var` -> F# `use`: the session is disposed at the end of the task { } block.
            writer.Write(
                $"use {Session!.Usage} = {_factory!.FSharpUsage}.{OpenSessionMethod}({_context!.FSharpUsage})");
        }

        if (_chain.Idempotency != IdempotencyStyle.None)
        {
            writer.BlankLine();
            writer.WriteComment("This message handler is configured for Eager idempotency checks");
            writer.Write(
                $"do! {_context!.FSharpUsage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation!.FSharpUsage})");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
