using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Runtime;

namespace Wolverine.RavenDb.Internals;

/// <summary>
/// Flushes the outgoing messages of a RavenDb unit of work once its session has been committed
/// </summary>
public static class RavenDbOutboxFlushing
{
    /// <summary>
    /// GH-5016: nothing can be trusted to run after a serverless request completes, so under
    /// <see cref="DurabilityMode.ServerlessWithOutbox"/> the messages are sent right now and their outbox
    /// documents deleted in one batch. A failed send stays in the outbox for the next durability pass
    /// </summary>
    public static Task FlushOutgoingMessagesAsync(MessageContext context)
    {
        if (context.Runtime.Options.Durability.Mode == DurabilityMode.ServerlessWithOutbox)
        {
            return context.SendAllOutgoingMessagesInlineAsync();
        }

        return context.FlushOutgoingMessagesAsync();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects RavenDbOutboxFlushing.GetMethod(nameof(FlushOutgoingMessagesAsync)) at codegen time. The target method is statically referenced via nameof, so the trimmer can see the reference and preserve the method.")]
    internal static MethodCall BuildFrame()
    {
        return new MethodCall(typeof(RavenDbOutboxFlushing), nameof(FlushOutgoingMessagesAsync))
        {
            CommentText = "Flush outgoing messages after the RavenDb session is committed"
        };
    }
}
