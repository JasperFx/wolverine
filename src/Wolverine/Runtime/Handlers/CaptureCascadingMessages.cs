using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

public class CaptureCascadingMessages : MethodCall
{
    private static readonly MethodInfo _method
        = ReflectionHelper.GetMethod<MessageContext>(x => x.EnqueueCascadingAsync(null))!;


    public CaptureCascadingMessages(Variable messages) : base(typeof(IMessageContext),
        _method)
    {
        Arguments[0] = messages;
        CommentText = "Outgoing, cascaded message";
    }
}