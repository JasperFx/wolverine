using System.Reflection;
using Marten;
using Marten.Linq.QueryHandlers;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine.Marten.Persistence.Operations;
using Wolverine.Persistence;
using Wolverine.Persistence.Codegen;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
/// GH-4505. The seam generated code calls when a logical deduplication claim rides the Marten
/// session's own transaction instead of being written on a separate connection.
///
/// <para>
/// The Marten twin of <see cref="IMessageDeduplicator" />, and it exists for the same reason: codegen
/// gets one dependency to resolve rather than reaching through
/// <c>IWolverineRuntime.Storage.Deduplication</c> and rendering a table name inline. Unlike that one,
/// every method here takes the session, because riding the transaction is the entire point.
/// </para>
/// </summary>
public interface IMartenDeduplicator
{
    /// <summary>
    /// Has <paramref name="deduplicationId" /> already been claimed? Asked through
    /// <paramref name="session" /> so it sees this transaction's own uncommitted claim.
    ///
    /// <para>
    /// Optimistic: two concurrent callers can both be told "no". See
    /// <see cref="ClaimExistsQuery" /> for why that is the only answer available before the handler
    /// runs, and what settles the race instead.
    /// </para>
    /// </summary>
    Task<bool> HasClaimAsync(IDocumentSession session, string deduplicationId, Type? ancillaryStoreMarker,
        CancellationToken cancellation);

    /// <summary>
    /// The same existence check as <see cref="HasClaimAsync" />, as a query handler to be enlisted in a
    /// batch the chain was already running — so a <c>[Deduplicated]</c> + <c>[WriteAggregate]</c>
    /// endpoint pays ONE round trip for the check and the aggregate load together.
    /// </summary>
    IQueryHandler<bool> ClaimExistsQuery(string deduplicationId, Type? ancillaryStoreMarker);

    /// <summary>
    /// Queue the claim onto <paramref name="session" />'s unit of work, so it commits with the
    /// handler's work and rolls back with it. Nothing is written until <c>SaveChangesAsync</c>.
    /// </summary>
    void QueueClaim(IDocumentSession session, string deduplicationId, Type? ancillaryStoreMarker);
}

internal class MartenDeduplicator : IMartenDeduplicator
{
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<MartenDeduplicator> _logger;

    public MartenDeduplicator(IWolverineRuntime runtime, ILogger<MartenDeduplicator> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public async Task<bool> HasClaimAsync(IDocumentSession session, string deduplicationId,
        Type? ancillaryStoreMarker, CancellationToken cancellation)
    {
        // Through a one-item batch rather than a second code path: the batched form is the one that
        // carries the real traffic, and a standalone implementation that drifted from it would be wrong
        // in exactly the case with no other frame to batch against -- and therefore no test alongside.
        var batch = session.CreateBatchQuery();
        var item = batch.AddItem(ClaimExistsQuery(deduplicationId, ancillaryStoreMarker));
        await batch.Execute(cancellation).ConfigureAwait(false);

        return await item.ConfigureAwait(false);
    }

    public IQueryHandler<bool> ClaimExistsQuery(string deduplicationId, Type? ancillaryStoreMarker)
    {
        return new DeduplicationClaimExistsHandler(tableFor(ancillaryStoreMarker), deduplicationId, logDuplicate);
    }

    public void QueueClaim(IDocumentSession session, string deduplicationId, Type? ancillaryStoreMarker)
    {
        // Stored rather than computed at read time, exactly as MessageDeduplicator does, so that
        // shortening the window later cannot retroactively un-claim ids recorded under the longer one.
        var expires = DateTimeOffset.UtcNow.Add(_runtime.Options.Durability.DeduplicationWindow);

        session.QueueOperation(
            new ClaimDeduplicationId(tableFor(ancillaryStoreMarker), deduplicationId, expires));
    }

    private void logDuplicate(string deduplicationId)
    {
        // Information rather than Debug, on the same reasoning as MessageDeduplicator: this is a
        // business event ("that work was already done"), it is rare by construction, and a duplicate
        // that vanishes silently is indistinguishable from a message that was lost.
        _logger.LogInformation(
            "Discarding duplicate work for logical deduplication id '{DeduplicationId}'; it was already claimed within the {Window} deduplication window",
            deduplicationId, _runtime.Options.Durability.DeduplicationWindow);
    }

    private string tableFor(Type? ancillaryStoreMarker)
    {
        var store = ancillaryStoreMarker == null
            ? _runtime.Storage
            : _runtime.Stores.FindAncillaryStore(ancillaryStoreMarker);

        if (store is not PostgresqlMessageStore database || !store.Deduplication.Enabled)
        {
            // Same failure, and the same reason for it, as MessageDeduplicator.TryClaimAsync: letting
            // this through would report the application as protected while every duplicate ran, with no
            // log line and no failing test to distinguish it from a working configuration.
            throw new InvalidOperationException(
                $"Logical message deduplication is enabled, but the message store backing this chain does not implement it. See {nameof(IDeduplicationStore)} for the providers that do. GH-4180");
        }

        return database.DeduplicationFullName;
    }
}

/// <summary>
/// GH-4505. Classification of the one failure the optimistic check cannot prevent: two genuinely
/// concurrent callers both read "not claimed", both queue the INSERT, and the second one to commit
/// trips the deduplication table's primary key.
/// </summary>
public static class MartenDeduplicationFailures
{
    /// <summary>
    /// <see cref="IsDuplicateDeduplicationClaim" /> as a <see cref="MethodInfo" />, for
    /// <see cref="RefuseDuplicateClaimAtCommitFrame" /> to render into an exception filter.
    /// </summary>
    public static MethodInfo Classifier { get; } =
        typeof(MartenDeduplicationFailures).GetMethod(nameof(IsDuplicateDeduplicationClaim))!;

    /// <summary>
    /// Was <paramref name="exception" /> a commit that lost the race for a logical deduplication id?
    ///
    /// <para>
    /// Scoped to the deduplication table by name, deliberately. A blanket "any unique violation"
    /// test would silently swallow a duplicate-key failure raised by the application's OWN documents
    /// and report it to the caller as "already handled" — which is the precise failure this feature
    /// exists to remove, reintroduced one layer up.
    /// </para>
    /// </summary>
    public static bool IsDuplicateDeduplicationClaim(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation
                                          && pg.TableName == DatabaseConstants.DeduplicationTableName)
            {
                return true;
            }

            if (e is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(IsDuplicateDeduplicationClaim))
            {
                return true;
            }
        }

        return false;
    }
}
