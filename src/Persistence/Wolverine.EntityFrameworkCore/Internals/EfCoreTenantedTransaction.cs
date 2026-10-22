using Microsoft.EntityFrameworkCore;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     Called from generated code: commits the transaction of a DbContext built for a tenant under managed
///     multi-tenancy. See <c>CommitTenantedDbContextTransaction</c>
/// </summary>
public static class EfCoreTenantedTransaction
{
    /// <summary>
    ///     E3 (durable compliance rig). Commit through the <see cref="EfCoreEnvelopeTransaction" /> the tenant DbContext
    ///     was enlisted with, exactly as the single-database path does. Committing the DbContext directly skipped the
    ///     one place the durable inbox row is marked Handled inside the handler's transaction, so the mark was left to
    ///     the durable receiver AFTER the commit, and a shutdown between the two ran the message again. The outbox
    ///     flush is not done here: <c>FlushOutboxAfterCommit</c> follows this frame (GH-4742)
    /// </summary>
    public static async Task CommitAsync(DbContext dbContext, MessageContext context,
        IEnumerable<IDomainEventScraper> scrapers, CancellationToken cancellation)
    {
        if (context.Transaction is EfCoreEnvelopeTransaction transaction &&
            ReferenceEquals(transaction.DbContext, dbContext))
        {
            await transaction.CommitAsync(cancellation, false).ConfigureAwait(false);
            return;
        }

        // Not the DbContext the context is enlisted with, so there is no envelope transaction to commit through:
        // what this frame always did
        foreach (var scraper in scrapers)
        {
            await scraper.ScrapeEvents(dbContext, context).ConfigureAwait(false);
        }

        // GH-3744: persist any envelopes the scrape just tracked, inside this transaction
        await dbContext.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await dbContext.Database.CommitTransactionAsync(cancellation).ConfigureAwait(false);
    }
}
