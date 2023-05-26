using Microsoft.EntityFrameworkCore;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

internal class DbContextOutbox<T> : MessageContext, IDbContextOutbox<T> where T : DbContext
{
    public DbContextOutbox(IWolverineRuntime runtime, T dbContext) : base(runtime)
    {
        DbContext = dbContext;

        Transaction = dbContext.BuildTransaction(this);
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

internal class DbContextOutbox : MessageContext, IDbContextOutbox
{
    public DbContextOutbox(IWolverineRuntime runtime) : base(runtime)
    {
    }

    public void Enroll(DbContext dbContext)
    {
        ActiveContext = dbContext;

        Transaction = dbContext.BuildTransaction(this);
    }

    public DbContext? ActiveContext { get; private set; }

    public async Task SaveChangesAndFlushMessagesAsync(CancellationToken token = default)
    {
        if (ActiveContext == null)
        {
            throw new InvalidOperationException("No active DbContext. Call Enroll() first");
        }

        await ActiveContext.SaveChangesAsync(token);

        if (ActiveContext.Database.CurrentTransaction != null)
        {
            await ActiveContext.Database.CommitTransactionAsync(token).ConfigureAwait(false);
        }

        await FlushOutgoingMessagesAsync();
    }
}