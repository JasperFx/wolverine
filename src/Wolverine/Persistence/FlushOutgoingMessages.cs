using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

public class FlushOutgoingMessages : MethodCall
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects FlushOutgoingMessages.GetMethod(nameof(FlushOutgoingMessages.FlushOutgoingMessagesAsync)) at codegen time. The target method is statically referenced via nameof, so the trimmer can see the reference and preserve the method; the suppression acknowledges the upstream RUC propagation.")]
    public FlushOutgoingMessages() : base(typeof(FlushOutgoingMessages), nameof(FlushOutgoingMessagesAsync))
    {
        CommentText = "Have to flush outgoing messages just in case Marten did nothing because of https://github.com/JasperFx/wolverine/issues/536";
    }

    /// <summary>
    ///     Called by generated code at the end of a handler or HTTP endpoint chain. GH-5886: this is the
    ///     pipeline's own flush, so it sends everything enqueued since an earlier flush, such as a message
    ///     published after the handler committed its own session. <see cref="MessageContext.MultiFlushMode" />
    ///     only governs explicit calls to <see cref="MessageContext.FlushOutgoingMessagesAsync" />
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Task FlushOutgoingMessagesAsync(MessageContext context)
    {
        return context.FlushOutgoingMessagesFromPipelineAsync();
    }
}
