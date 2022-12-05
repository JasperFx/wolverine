using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using TypeExtensions = Wolverine.Util.TypeExtensions;

namespace Wolverine.Runtime.Handlers;

public class HandlerCall : MethodCall
{
    public HandlerCall(Type handlerType, MethodInfo method) : base(handlerType, method)
    {
        MessageType = method.MessageType()!;

        if (MessageType == null)
        {
            throw new ArgumentOutOfRangeException(nameof(method),
                $"Method {handlerType.FullName}.{method.Name} has no message type");
        }
    }

    public Type MessageType { get; }

    public static bool IsCandidate(MethodInfo method)
    {
        if (!method.GetParameters().Any())
        {
            return false;
        }

        if (method.DeclaringType == typeof(object))
        {
            return false;
        }

        if (method.IsSpecialName)
        {
            return false;
        }

        var messageType = method.MessageType();
        if (messageType == null)
        {
            return false;
        }

        var hasOutput = method.ReturnType != typeof(void);

        if (TypeExtensions.IsValueTuple(method.ReturnType))
        {
            return true;
        }

        return !hasOutput || !method.ReturnType.IsPrimitive;
    }

    public static HandlerCall For<T>(Expression<Action<T>> method)
    {
        return new HandlerCall(typeof(T), ReflectionHelper.GetMethod(method));
    }

    public bool CouldHandleOtherMessageType(Type messageType)
    {
        if (messageType == MessageType)
        {
            return false;
        }

        return messageType.CanBeCastTo(MessageType);
    }

    internal HandlerCall Clone(Type messageType)
    {
        var clone = new HandlerCall(HandlerType, Method);
        clone.Aliases.Add(MessageType, messageType);


        return clone;
    }
}