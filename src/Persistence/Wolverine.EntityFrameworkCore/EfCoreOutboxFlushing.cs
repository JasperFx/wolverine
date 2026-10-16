using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Flushes the outgoing messages of an EF Core unit of work once its transaction has been committed
/// </summary>
public static class EfCoreOutboxFlushing
{
    /// <summary>
    /// GH-5016: nothing can be trusted to run after a serverless request completes, so under
    /// <see cref="DurabilityMode.ServerlessWithOutbox"/> the messages are sent right now and their outbox
    /// rows deleted in one batch. A failed send stays in the outbox for the next durability pass
    /// </summary>
    public static Task FlushOutgoingMessagesAsync(MessageContext context)
    {
        if (context.Runtime.Options.Durability.Mode == DurabilityMode.ServerlessWithOutbox)
        {
            return context.SendAllOutgoingMessagesInlineAsync();
        }

        return context.FlushOutgoingMessagesAsync();
    }
}
