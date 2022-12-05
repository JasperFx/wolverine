using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
///     A durable, outbox-ed message publisher using EF Core for persistence
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IDbContextOutbox<T> : IMessagePublisher where T : DbContext
{
    /// <summary>
    ///     The current DbContext for this outbox
    /// </summary>
    T DbContext { get; }


    /// <summary>
    ///     Saves outstanding changes in the DbContext and flushes outbox messages to the
    ///     sending agents
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task SaveChangesAndFlushMessagesAsync(CancellationToken token = default);

    /// <summary>
    ///     Calling this
    ///     method will force the outbox to send out any outstanding messages
    ///     that were captured as part of processing the transaction if you call SaveChangesAsync()
    ///     directly on the DbContext
    /// </summary>
    /// <returns></returns>
    Task FlushOutgoingMessagesAsync();
}

/// <summary>
///     Wrapped messaging outbox for an EF Core DbContext
/// </summary>
public interface IDbContextOutbox : IMessagePublisher
{
    /// <summary>
    ///     The active DbContext
    /// </summary>
    DbContext? ActiveContext { get; }

    /// <summary>
    ///     Enroll a DbContext into durable outbox sending
    /// </summary>
    /// <param name="dbContext"></param>
    void Enroll(DbContext dbContext);

    /// <summary>
    ///     Saves outstanding changes in the DbContext and flushes outbox messages to the
    ///     sending agents
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task SaveChangesAndFlushMessagesAsync(CancellationToken token = default);

    /// <summary>
    ///     Calling this
    ///     method will force the outbox to send out any outstanding messages
    ///     that were captured as part of processing the transaction if you call SaveChangesAsync()
    ///     directly on the DbContext
    /// </summary>
    /// <returns></returns>
    Task FlushOutgoingMessagesAsync();
}