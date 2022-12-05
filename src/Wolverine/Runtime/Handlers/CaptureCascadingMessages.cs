using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

internal class CaptureCascadingMessages : MethodCall
{
    private static readonly MethodInfo _method =
#pragma warning disable CS8625
        ReflectionHelper.GetMethod<MessageContext>(x => x.EnqueueCascadingAsync(null));
#pragma warning restore CS8625


    public CaptureCascadingMessages(Variable messages) : base(typeof(IMessageContext),
        _method)
    {
        Arguments[0] = messages;
        CommentText = "Outgoing, cascaded message";
    }
}