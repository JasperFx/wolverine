using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Runtime;

namespace Wolverine.Middleware;

internal class MethodCallAgainstMessage : MethodCall
{
    private readonly Type _messageType;

    public MethodCallAgainstMessage(Type handlerType, MethodInfo method, Type messageType) : base(handlerType, method)
    {
        _messageType = messageType;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        Arguments[0] = new Variable(_messageType,
            $"({_messageType.FullNameInCode()})context.{nameof(MessageContext.Envelope)}.{nameof(Envelope.Message)}");
        foreach (var variable in base.FindVariables(chain)) yield return variable;
    }
}