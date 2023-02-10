using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Middleware;

internal class MethodCallAgainstMessage : MethodCall
{
    private readonly Type _inputType;
    private Variable? _message;

    public MethodCallAgainstMessage(Type handlerType, MethodInfo method, Type inputType) : base(handlerType, method)
    {
        _inputType = inputType;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _message = chain.FindVariable(_inputType);
        yield return _message;

        Arguments[0] = _message;
        foreach (var variable in base.FindVariables(chain)) yield return variable;
    }
}