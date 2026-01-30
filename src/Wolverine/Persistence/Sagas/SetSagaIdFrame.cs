using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Frame that generates code to set the saga ID on the MessageContext
/// so that cascading messages will have the correct saga ID attached.
/// </summary>
internal class SetSagaIdFrame : SyncFrame
{
    private readonly Variable _sagaId;
    private Variable? _context;

    public SetSagaIdFrame(Variable sagaId)
    {
        _sagaId = sagaId;
        uses.Add(_sagaId);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_context!.Usage}.{nameof(MessageContext.SetSagaId)}({_sagaId.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
