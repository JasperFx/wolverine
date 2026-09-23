using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Polecat;
using Wolverine.Persistence;
using Wolverine.Persistence.Codegen;
using Wolverine.Persistence.Durability;
using Wolverine.Polecat.Persistence.Operations;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;

namespace Wolverine.Polecat;

/// <summary>
/// GH-4570. The seam generated code calls when a logical deduplication claim rides the Polecat session's
/// own transaction instead of being written on a separate connection.
///
/// <para>
/// The Polecat twin of <see cref="IMessageDeduplicator" />, and it exists for the same reason: codegen gets
/// one dependency to resolve rather than reaching through <c>IWolverineRuntime.Storage.Deduplication</c>
/// and rendering a table name inline. Unlike that one, both methods take the session, because riding the
/// transaction is the entire point.
/// </para>
/// </summary>
public interface IPolecatDeduplicator
{
    /// <summary>
    /// Has <paramref name="deduplicationId" /> already been claimed? Asked through
    /// <paramref name="session" />, so the read runs on the connection the handler is about to commit on.
    ///
    /// <para>
    /// Optimistic: two concurrent callers can both be told "no", because neither claim is committed yet
    /// and so neither is visible to the other. The deduplication table's primary key settles that at
    /// commit — see <see cref="PolecatDeduplicationFailures" />.
    /// </para>
    ///
    /// <para>
    /// Unlike the Marten twin this cannot share a round trip with the chain's other reads:
    /// <c>Polecat.Batching.IBatchedQuery</c> has no <c>AddItem&lt;T&gt;(IQueryHandler&lt;T&gt;)</c>
    /// equivalent to enlist a raw-SQL existence check into. That is a missed optimisation, not a
    /// correctness gap — the claim still rides the transaction either way.
    /// </para>
    /// </summary>
    Task<bool> HasClaimAsync(IDocumentSession session, string deduplicationId, Type? ancillaryStoreMarker,
        CancellationToken cancellation);

    /// <summary>
    /// Enlist the claim in <paramref name="session" />'s transaction, so it commits with the handler's
    /// work and rolls back with it. Nothing is written until <c>SaveChangesAsync</c>.
    /// </summary>
    void QueueClaim(IDocumentSession session, string deduplicationId, Type? ancillaryStoreMarker);
}

internal class PolecatDeduplicator : IPolecatDeduplicator
{
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<PolecatDeduplicator> _logger;

    public PolecatDeduplicator(IWolverineRuntime runtime, ILogger<PolecatDeduplicator> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public async Task<bool> HasClaimAsync(IDocumentSession session, string deduplicationId,
        Type? ancillaryStoreMarker, CancellationToken cancellation)
    {
        var table = tableFor(ancillaryStoreMarker);

        // Through the session's own AdvancedSql rather than the message store's DbDataSource. The read
        // would be correct either way -- it can only ever see committed claims, and this transaction has
        // not written one yet -- but routing it through the session keeps the whole feature on one
        // connection, which is what the Fisher twin genuinely requires and what keeps the two readable as
        // the same design.
        // '?' is the placeholder Polecat parses, NOT '@p0' -- the XML docs describe what a placeholder is
        // rendered INTO, which reads as though the SQL should carry it. Writing '@p0' here throws
        // "Expected at least 1 placeholder(s) '?' but found 0" at the first deduplicated message.
        var matches = await session.AdvancedSql
            .QueryAsync<int>(
                $"select 1 from {table} where {DatabaseConstants.DeduplicationId} = ?",
                cancellation, deduplicationId)
            .ConfigureAwait(false);

        var claimed = matches.Any();

        if (claimed)
        {
            // Information rather than Debug, on the same reasoning as MessageDeduplicator: this is a
            // business event ("that work was already done"), it is rare by construction, and a duplicate
            // that vanishes silently is indistinguishable from a message that was lost.
            _logger.LogInformation(
                "Discarding duplicate work for logical deduplication id '{DeduplicationId}'; it was already claimed within the {Window} deduplication window",
                deduplicationId, _runtime.Options.Durability.DeduplicationWindow);
        }

        return claimed;
    }

    public void QueueClaim(IDocumentSession session, string deduplicationId, Type? ancillaryStoreMarker)
    {
        // Stored rather than computed at read time, exactly as MessageDeduplicator does, so that
        // shortening the window later cannot retroactively un-claim ids recorded under the longer one.
        var expires = DateTimeOffset.UtcNow.Add(_runtime.Options.Durability.DeduplicationWindow);

        session.AddTransactionParticipant(
            new ClaimDeduplicationIdParticipant(tableFor(ancillaryStoreMarker), deduplicationId, expires));
    }

    private string tableFor(Type? ancillaryStoreMarker)
    {
        var store = ancillaryStoreMarker == null
            ? _runtime.Storage
            : _runtime.Stores.FindAncillaryStore(ancillaryStoreMarker);

        if (store is not SqlServerMessageStore database || !store.Deduplication.Enabled)
        {
            // Same failure, and the same reason for it, as MessageDeduplicator.TryClaimAsync: letting this
            // through would report the application as protected while every duplicate ran, with no log
            // line and no failing test to distinguish it from a working configuration.
            throw new InvalidOperationException(
                $"Logical message deduplication is enabled, but the message store backing this chain does not implement it. See {nameof(IDeduplicationStore)} for the providers that do. GH-4180");
        }

        return database.DeduplicationFullName;
    }
}

/// <summary>
/// GH-4570. Classification of the one failure the optimistic check cannot prevent: two genuinely
/// concurrent callers both read "not claimed", both enlist the INSERT, and the second one to commit trips
/// the deduplication table's primary key.
/// </summary>
public static class PolecatDeduplicationFailures
{
    /// <summary>
    /// <see cref="IsDuplicateDeduplicationClaim" /> as a <see cref="MethodInfo" />, for
    /// <see cref="RefuseDuplicateClaimAtCommitFrame" /> to render into an exception filter.
    /// </summary>
    public static MethodInfo Classifier { get; } =
        typeof(PolecatDeduplicationFailures).GetMethod(nameof(IsDuplicateDeduplicationClaim))!;

    /// <summary>
    /// Was <paramref name="exception" /> a commit that lost the race for a logical deduplication id?
    ///
    /// <para>
    /// Scoped to the deduplication table, deliberately, and this matters more on SQL Server than it does
    /// on PostgreSQL. <see cref="SqlException" /> carries no table name, so a blanket
    /// <c>Number == 2627 || 2601</c> test — which is what <c>PolecatIntegration</c>'s existing discard rule
    /// does, see GH-4565 — matches a duplicate-key failure raised by the application's OWN documents just
    /// as readily, and would report it to the caller as "already handled". The table name is in the
    /// message text, which is the only place SQL Server puts it.
    /// </para>
    /// </summary>
    public static bool IsDuplicateDeduplicationClaim(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is SqlException sql && (sql.Number == 2627 || sql.Number == 2601)
                                      && sql.Message.Contains(DatabaseConstants.DeduplicationTableName,
                                          StringComparison.OrdinalIgnoreCase))
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
