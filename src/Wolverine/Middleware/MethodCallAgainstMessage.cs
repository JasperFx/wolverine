using System;
using System.Collections.Generic;
using System.Reflection;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

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
        var messageType = Method.MessageType();
        Arguments[0] = new Variable(messageType,
            $"({messageType.FullNameInCode()})context.{nameof(MessageContext.Envelope)}.{nameof(Envelope.Message)}");
        foreach (var variable in base.FindVariables(chain))
        {
            yield return variable;
        }

        
    }
}