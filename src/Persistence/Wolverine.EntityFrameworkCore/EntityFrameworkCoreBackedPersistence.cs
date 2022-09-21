using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
///     Add to your Wolverine application to opt into EF Core-backed
///     transaction and saga persistence middleware.
///     Warning! This has to be used in conjunction with a Wolverine
///     database package
/// </summary>
public class EntityFrameworkCoreBackedPersistence : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        var frameProvider = new EFCorePersistenceFrameProvider();
        options.Advanced.CodeGeneration.SetSagaPersistence(frameProvider);
        options.Advanced.CodeGeneration.SetTransactions(frameProvider);

        options.Services.AddScoped(typeof(IDbContextOutbox<>), typeof(DbContextOutbox<>));
        options.Services.AddScoped<IDbContextOutbox, DbContextOutbox>();
    }
}

public interface IDbContextOutbox : IMessagePublisher
{
    /// <summary>
    /// Enroll a DbContext into durable outbox sending
    /// </summary>
    /// <param name="dbContext"></param>
    void Enroll(DbContext dbContext);
    
    /// <summary>
    /// The active DbContext
    /// </summary>
    DbContext? ActiveContext { get; }
    
    /// <summary>
    /// Saves outstanding changes in the DbContext and flushes outbox messages to the
    /// sending agents
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task SaveChangesAndFlushMessagesAsync(CancellationToken token = default);
    
    /// <summary>
    /// Calling this
    /// method will force the outbox to send out any outstanding messages
    /// that were captured as part of processing the transaction if you call SaveChangesAsync()
    /// directly on the DbContext
    /// </summary>
    /// <returns></returns>
    Task FlushOutgoingMessagesAsync();
}

internal class DbContextOutbox : MessageContext, IDbContextOutbox
{
    public DbContextOutbox(IWolverineRuntime runtime) : base(runtime)
    {
    }

    public void Enroll(DbContext dbContext)
    {
        ActiveContext = dbContext;

        Transaction = new EfCoreEnvelopeTransaction(dbContext, this);
    }

    public DbContext? ActiveContext { get; private set; }
    public async Task SaveChangesAndFlushMessagesAsync(CancellationToken token = default)
    {
        if (ActiveContext == null) throw new InvalidOperationException("No active DbContext. Call Enroll() first");
        
        await ActiveContext.SaveChangesAsync(token);
        
        if (ActiveContext.Database.CurrentTransaction != null)
        {
            await ActiveContext.Database.CommitTransactionAsync(token).ConfigureAwait(false);
        }
        await FlushOutgoingMessagesAsync();
    }
}

/// <summary>
/// A durable, outbox-ed message publisher using EF Core for persistence
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IDbContextOutbox<T> : IMessagePublisher where T : DbContext
{
    /// <summary>
    /// The current DbContext for this outbox
    /// </summary>
    T DbContext { get; }

    
    /// <summary>
    /// Saves outstanding changes in the DbContext and flushes outbox messages to the
    /// sending agents
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task SaveChangesAndFlushMessagesAsync(CancellationToken token = default);
    
    /// <summary>
    /// Calling this
    /// method will force the outbox to send out any outstanding messages
    /// that were captured as part of processing the transaction if you call SaveChangesAsync()
    /// directly on the DbContext
    /// </summary>
    /// <returns></returns>
    Task FlushOutgoingMessagesAsync();
}

internal class DbContextOutbox<T> : MessageContext, IDbContextOutbox<T> where T : DbContext
{
    public DbContextOutbox(IWolverineRuntime runtime, T dbContext) : base(runtime)
    {
        DbContext = dbContext;

        Transaction = new EfCoreEnvelopeTransaction(dbContext, this);
    }

    public T DbContext { get; }
    public async Task SaveChangesAndFlushMessagesAsync(CancellationToken token = default)
    {
        await DbContext.SaveChangesAsync(token);
        if (DbContext.Database.CurrentTransaction != null)
        {
            await DbContext.Database.CommitTransactionAsync(token).ConfigureAwait(false);
        }
        await FlushOutgoingMessagesAsync();
    }
}
