using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.RavenDb.Internals;

internal class TransactionalFrame : Frame
{
    private readonly IChain _chain;
    private Variable? _cancellation;
    private Variable? _context;
    private bool _createsSession;
    private Variable? _store;

    public TransactionalFrame(IChain chain) : base(true)
    {
        _chain = chain;
    }

    public Variable? Session { get; private set; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        Session = chain.TryFindVariable(typeof(IAsyncDocumentSession), VariableSource.NotServices);
        if (Session == null)
        {
            _createsSession = true;
            Session = new Variable(typeof(IAsyncDocumentSession), this);

            _store = chain.FindVariable(typeof(IDocumentStore));
            yield return _store;
        }
        else if (Session.Creator is BuildAsyncDocumentSession opened)
        {
            // GH-5678. Something ahead of this frame, typically middleware, asked for the session first, so it was
            // opened by the session source rather than here. It is still the session this chain saves, so it is
            // enlisted in the outbox where it is opened, before that middleware runs
            opened.EnlistInOutbox(_chain is SagaChain);
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
            writer.WriteComment("Open a new document session ");
            writer.WriteComment("message context to support the outbox functionality");
            writer.Write(
                $"using var {Session!.Usage} = {_store!.Usage}.{nameof(IDocumentStore.OpenAsyncSession)}();");
            writer.Write($"{_context!.Usage}.{nameof(MessageContext.EnlistInOutbox)}(new {typeof(RavenDbEnvelopeTransaction).FullName}({Session!.Usage}, {_context.Usage}));");

            if (_chain is SagaChain)
            {
                writer.WriteComment("Use optimistic concurrency for sagas");
                writer.Write($"{Session!.Usage}.Advanced.UseOptimisticConcurrency = true;");
            }
        }

        Next?.GenerateCode(method, writer);
    }
}