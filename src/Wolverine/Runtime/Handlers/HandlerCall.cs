using System.Linq.Expressions;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

public class HandlerCall : MethodCall
{
    public HandlerCall(Type handlerType, string methodName) : base(handlerType, methodName)
    {
        MessageType = Method.MessageType()!;

        if (MessageType == null)
        {
            throw new ArgumentOutOfRangeException(nameof(Method),
                $"Method {handlerType.FullName}.{Method.Name} has no message type");
        }

        CommentText = "The actual message execution";
    }

    public HandlerCall(Type handlerType, MethodInfo method) : base(handlerType, method)
    {
        if (method == null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        MessageType = method.MessageType()!;

        if (MessageType == null)
        {
            throw new ArgumentOutOfRangeException(nameof(method),
                $"Method {handlerType.FullName}.{method.Name} has no message type");
        }

        CommentText = "The actual message execution";
    }

    public Type MessageType { get; }

    public new static HandlerCall For<T>(Expression<Action<T>> method)
    {
        return new HandlerCall(typeof(T), ReflectionHelper.GetMethod(method)!);
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