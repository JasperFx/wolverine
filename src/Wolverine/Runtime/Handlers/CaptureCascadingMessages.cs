using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

public class CaptureCascadingMessages : MethodCall
{
    private static readonly MethodInfo _method
        = ReflectionHelper.GetMethod<MessageContext>(x => x.EnqueueCascadingAsync(null))!;


    // Target type is the concrete MessageContext, not IMessageContext: the
    // public IMessageContext interface does not expose EnqueueCascadingAsync,
    // so a MethodCall declared against the interface forces JasperFx to emit
    // a `((IMessageContext)messageContext).EnqueueCascadingAsync(...)` cast,
    // which fails to compile (CS1061). MessageBusSource (post-#2959) produces
    // a concrete MessageContext-typed Variable, so this lookup finds it
    // without any cast — matches the FlushOutgoingMessages MethodCall shape.
    // See wolverine#2963 (failing http issue_2917 tests under JasperFx 2.2.3).
    public CaptureCascadingMessages(Variable messages) : base(typeof(MessageContext),
        _method)
    {
        Arguments[0] = messages;
        CommentText = "Outgoing, cascaded message";
    }
}