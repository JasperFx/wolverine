using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Middleware;

/// <summary>
/// Execute a series of inner frames if the specified variable is not null
/// </summary>
public class IfNotNullFrame : CompositeFrame
{
    private readonly Variable _variable;

    public IfNotNullFrame(Variable variable, params Frame[] inner) : base(inner)
    {
        _variable = variable ?? throw new ArgumentNullException(nameof(variable));
        uses.Add(variable);

        Inners = inner;
    }
    
    public IReadOnlyList<Frame> Inners { get; }

    protected override void generateCode(GeneratedMethod method, ISourceWriter writer, Frame inner)
    {
        writer.Write($"BLOCK:if ({_variable.Usage} != null)");
        inner.GenerateCode(method, writer);
        writer.FinishBlock();
    }
}