using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Shims.MassTransit;

/// <summary>
/// Code generation variable source that creates <see cref="WolverineConsumeContext{T}"/>
/// instances from the current <see cref="IMessageContext"/> and the message.
/// </summary>
internal class ConsumeContextVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type.Closes(typeof(ConsumeContext<>));
    }

    public Variable Create(Type type)
    {
        var messageType = type.GetGenericArguments()[0];
        return new ConsumeContextFrame(messageType).Variable;
    }
}

internal class ConsumeContextFrame : SyncFrame
{
    private readonly Type _messageType;
    private Variable? _context;
    private Variable? _message;

    public ConsumeContextFrame(Type messageType)
    {
        _messageType = messageType;
        var consumeContextType = typeof(WolverineConsumeContext<>).MakeGenericType(messageType);
        Variable = new Variable(typeof(ConsumeContext<>).MakeGenericType(messageType), this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(IMessageContext));
        yield return _context;

        _message = chain.FindVariable(_messageType);
        yield return _message;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var concreteType = typeof(WolverineConsumeContext<>).MakeGenericType(_messageType);
        writer.WriteLine(
            $"var {Variable.Usage} = new {concreteType.FullNameInCode()}({_context!.Usage}, {_message!.Usage});");

        Next?.GenerateCode(method, writer);
    }
}
