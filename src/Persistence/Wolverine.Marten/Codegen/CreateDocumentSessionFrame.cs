using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Wolverine.Configuration;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Marten.Codegen;

internal class CreateDocumentSessionFrame : Frame
{
    private readonly IChain _chain;
    private Variable? _cancellation;
    private Variable? _context;
    private bool _createsSession;
    private Variable? _factory;

    public CreateDocumentSessionFrame(IChain chain) : base(true)
    {
        _chain = chain;
    }

    public Variable? Session { get; private set; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        Session = chain.TryFindVariable(typeof(IDocumentSession), VariableSource.NotServices);
        if (Session == null)
        {
            _createsSession = true;
            Session = new Variable(typeof(IDocumentSession), this);

            _factory = chain.FindVariable(typeof(OutboxedSessionFactory));
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
                $"using var {Session!.Usage} = {_factory!.Usage}.{nameof(OutboxedSessionFactory.OpenSession)}({_context!.Usage});");
        }

        if (_chain.Idempotency == IdempotencyStyle.Eager)
        {
            writer.BlankLine();
            writer.WriteComment("This message handler is configured for Eager idempotency checks");
            
            writer.Write($"await {_context.Usage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }
}