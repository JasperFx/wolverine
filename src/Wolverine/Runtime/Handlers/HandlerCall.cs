using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

public class HandlerCall : MethodCall
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects handlerType.GetMethod(methodName) at codegen time. handlerType flows from Wolverine's handler discovery, which roots application handler types via opts.Discovery.IncludeAssembly / IncludeType registration. AOT consumers run pre-generated handlers via TypeLoadMode.Static so the reflective close never fires.")]
    public HandlerCall(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type handlerType, string methodName) : base(handlerType, methodName)
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

    internal IEnumerable<Attribute> GetAllAttributes()
    {
        return HandlerType.GetCustomAttributes().Concat(Method.GetCustomAttributes());
    }
}