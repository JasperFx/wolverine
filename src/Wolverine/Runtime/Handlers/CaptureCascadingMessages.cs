using System.Reflection;
using JasperFx.CodeGeneration;
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

/// <summary>
/// Publishes a value produced inside a try/catch catch block — e.g. a message (or OutgoingMessages,
/// or IEnumerable of messages) returned from an OnException middleware method — as a cascading message.
/// </summary>
/// <remarks>
/// This mirrors <see cref="CaptureCascadingMessages"/> but, like the HTTP ProblemDetails catch frame,
/// deliberately does NOT yield the captured variable from <see cref="FindVariables"/>. That variable is
/// created by an earlier frame in the same catch block (the OnException call); exposing the dependency
/// would make the code-gen arranger pre-link the two frames' <c>Next</c> pointers, which then collides
/// with <see cref="Wolverine.Middleware.TryCatchFinallyFrame"/>'s own manual chaining and throws
/// "Frame chain is being re-arranged". <see cref="MessageContext.EnqueueCascadingAsync"/> already
/// unwraps OutgoingMessages and IEnumerable internally, so a single frame covers every return shape.
/// </remarks>
internal sealed class CaptureCascadingMessagesInCatch : AsyncFrame
{
    private readonly Variable _cascaded;
    private Variable? _context;

    public CaptureCascadingMessagesInCatch(Variable cascaded)
    {
        _cascaded = cascaded;
        uses.Add(cascaded);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Cascade the message returned from the exception handler");
        writer.Write(
            $"await {_context!.Usage}.{nameof(MessageContext.EnqueueCascadingAsync)}({_cascaded.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}