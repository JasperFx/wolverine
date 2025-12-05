using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

public class DbContextOutbox<T> : MessageContext, IDbContextOutbox<T> where T : DbContext
{
    private readonly IEnumerable<IDomainEventScraper> _scrapers;

    public DbContextOutbox(IWolverineRuntime runtime, T dbContext, IEnumerable<IDomainEventScraper> scrapers) : base(runtime)
    {
        _scrapers = scrapers;
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

public class DbContextOutbox : MessageContext, IDbContextOutbox
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