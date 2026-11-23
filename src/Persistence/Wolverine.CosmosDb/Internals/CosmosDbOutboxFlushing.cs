using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Runtime;

namespace Wolverine.CosmosDb.Internals;

/// <summary>
/// Flushes the outgoing messages of a Cosmos DB unit of work once its writes are done
/// </summary>
public static class CosmosDbOutboxFlushing
{
    /// <summary>
    /// GH-5016: nothing can be trusted to run after a serverless request completes, so under
    /// <see cref="DurabilityMode.ServerlessWithOutbox"/> the messages are sent right now and their outbox
    /// documents deleted in one batch. A failed send stays in the outbox for the next durability pass
    /// </summary>
    public static Task FlushOutgoingMessagesAsync(MessageContext context)
    {
        // GH-5886: the flush after the chain's own commit, so it sends everything enqueued since an earlier flush.
        // Under ServerlessWithOutbox it sends inline
        return context.FlushOutgoingMessagesFromPipelineAsync();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects CosmosDbOutboxFlushing.GetMethod(nameof(FlushOutgoingMessagesAsync)) at codegen time. The target method is statically referenced via nameof, so the trimmer can see the reference and preserve the method.")]
    internal static MethodCall BuildFrame()
    {
        return new MethodCall(typeof(CosmosDbOutboxFlushing), nameof(FlushOutgoingMessagesAsync))
        {
            CommentText = "Flush outgoing messages after the Cosmos DB writes"
        };
    }
}
