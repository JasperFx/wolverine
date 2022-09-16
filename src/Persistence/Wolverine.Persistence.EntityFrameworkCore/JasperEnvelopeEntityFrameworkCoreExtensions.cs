using System;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Wolverine.Persistence.EntityFrameworkCore;

public static class WolverineEntityFrameworkCoreConfigurationExtensions
{
    /// <summary>
    ///     Uses Entity Framework Core for Saga persistence and transactional
    ///     middleware
    /// </summary>
    /// <param name="options"></param>
    public static void UseEntityFrameworkCorePersistence(this WolverineOptions options)
    {
        options.Include<EntityFrameworkCoreBackedPersistence>();
    }
}

public static class WolverineEnvelopeEntityFrameworkCoreExtensions
{
    /// <summary>
    ///     Persist the active DbContext and flush any persisted messages to the sending
    ///     process to complete the "Outbox"
    /// </summary>
    /// <param name="context"></param>
    /// <param name="messages"></param>
    /// <param name="cancellation"></param>
    public static async Task SaveChangesAndFlushMessagesAsync(this DbContext context, IMessageContext messages,
        CancellationToken cancellation = default)
    {
        await context.SaveChangesAsync(cancellation);
        var tx = context.Database.CurrentTransaction?.GetDbTransaction();
        if (tx != null)
        {
            try
            {
                await tx.CommitAsync(cancellation);
            }
            catch (Exception e)
            {
                if (!e.Message.Contains("has completed"))
                {
                    throw;
                }
            }
        }

        await messages.FlushOutgoingMessagesAsync();
    }

    /// <summary>
    ///     Enlists the current IMessagingContext in the EF Core DbContext's transaction
    ///     lifecycle. Note, you will need to call IMessageContext.SendAllQueuedOutgoingMessages()
    ///     to actually have the outgoing messages sent
    /// </summary>
    /// <param name="messaging"></param>
    /// <param name="dbContext"></param>
    /// <returns></returns>
    public static Task EnlistInOutboxAsync(this IMessagePublisher messaging, DbContext dbContext)
    {
        var executionContext = messaging.As<IMessageContext>();
        var transaction = new EfCoreEnvelopeOutbox(dbContext, executionContext);

        return executionContext.EnlistInOutboxAsync(transaction);
    }
}
