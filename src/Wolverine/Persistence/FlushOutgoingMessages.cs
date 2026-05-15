using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

public class FlushOutgoingMessages : MethodCall
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects MessageContext.GetMethod(nameof(MessageContext.FlushOutgoingMessagesAsync)) at codegen time. The target method is statically referenced via nameof, so the trimmer can see the reference and preserve the method; the suppression acknowledges the upstream RUC propagation.")]
    public FlushOutgoingMessages() : base(typeof(MessageContext), nameof(MessageContext.FlushOutgoingMessagesAsync))
    {
        CommentText = "Have to flush outgoing messages just in case Marten did nothing because of https://github.com/JasperFx/wolverine/issues/536";
    }
}