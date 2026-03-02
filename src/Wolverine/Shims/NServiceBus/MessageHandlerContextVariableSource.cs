using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// Code generation variable source that creates <see cref="WolverineMessageHandlerContext"/>
/// instances from the current <see cref="IMessageContext"/>.
/// This eliminates the need for service location when resolving <see cref="IMessageHandlerContext"/>
/// in handler methods.
/// </summary>
internal class MessageHandlerContextVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IMessageHandlerContext);
    }

    public Variable Create(Type type)
    {
        return new MessageHandlerContextFrame().Variable;
    }
}

internal class MessageHandlerContextFrame : SyncFrame
{
    private Variable? _context;

    public MessageHandlerContextFrame()
    {
        Variable = new Variable(typeof(IMessageHandlerContext), this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(IMessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"var {Variable.Usage} = new {typeof(WolverineMessageHandlerContext).FullNameInCode()}({_context!.Usage});");

        Next?.GenerateCode(method, writer);
    }
}
