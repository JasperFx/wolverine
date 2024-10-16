using JasperFx.CodeGeneration.Frames;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

public class FlushOutgoingMessages : MethodCall
{
    public FlushOutgoingMessages() : base(typeof(MessageContext), nameof(MessageContext.FlushOutgoingMessagesAsync))
    {
        CommentText = "Have to flush outgoing messages just in case Marten did nothing because of https://github.com/JasperFx/wolverine/issues/536";
    }
}