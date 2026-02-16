using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Azure.Cosmos;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.CosmosDb.Internals;

internal class TransactionalFrame : Frame
{
    private readonly IChain _chain;
    private Variable? _cancellation;
    private Variable? _context;

    public TransactionalFrame(IChain chain) : base(true)
    {
        _chain = chain;
    }

    public Variable? Container { get; private set; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        // Container is resolved from DI (registered by UseCosmosDbPersistence)
        Container = chain.FindVariable(typeof(Container));
        yield return Container;

        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices);

        if (_context != null)
        {
            yield return _context;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_context != null)
        {
            writer.BlankLine();
            writer.WriteComment("Enlist in CosmosDB outbox transaction");
            writer.Write(
                $"{_context.Usage}.{nameof(MessageContext.EnlistInOutbox)}(new {typeof(CosmosDbEnvelopeTransaction).FullName}({Container!.Usage}, {_context.Usage}));");
        }

        Next?.GenerateCode(method, writer);
    }
}
