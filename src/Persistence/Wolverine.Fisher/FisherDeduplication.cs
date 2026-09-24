using System.Reflection;
using Fisher;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Codegen;
using Wolverine.Persistence.Durability;
using Wolverine.Fisher.Persistence.Operations;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Sqlite;

namespace Wolverine.Fisher;

/// <summary>
/// GH-4571. The seam generated code calls when a logical deduplication claim rides the Fisher session's
/// own transaction instead of being written on a separate connection.
///
/// <para>
/// The Fisher twin of <see cref="IMessageDeduplicator" /> and of <c>IPolecatDeduplicator</c>. On Fisher
/// this is not an optimisation: Wolverine's message store is built from
/// <c>new WolverineSqliteDataSource(store.Options.ConnectionString)</c> — the same SQLite <b>file</b>, a
/// different pooled connection. Two connections to one file are two writers, and the second blocks on the
/// first from inside the first one's transaction. That is a self-deadlock, and it presents as a hang
/// rather than an error, so both methods take the session.
/// </para>
/// </summary>
public interface IFisherDeduplicator
{
    /// <summary>
    /// Has <paramref name="deduplicationId" /> already been claimed? Asked through
    /// <paramref name="session" />, so the read runs on the connection the handler is about to commit on.
    ///
    /// <para>
    /// Optimistic: two concurrent callers can both be told "no", because neither claim is committed yet
    /// and so neither is visible to the other. The deduplication table's primary key settles that at
    /// commit — see <see cref="FisherDeduplicationFailures" />.
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

internal class FisherDeduplicator : IFisherDeduplicator
{
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<FisherDeduplicator> _logger;

    public FisherDeduplicator(IWolverineRuntime runtime, ILogger<FisherDeduplicator> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public async Task<bool> HasClaimAsync(IDocumentSession session, string deduplicationId,
        Type? ancillaryStoreMarker, CancellationToken cancellation)
    {
        var table = tableFor(ancillaryStoreMarker);

        // Through the session's own AdvancedSql rather than the message store's DbDataSource, and here
        // that is load-bearing rather than tidy: the store's data source is a second connection to the
        // same file, and a read on it while this session holds the write lock is the deadlock Fisher's
        // own ITransactionParticipant docs warn about.
        // '?' is the placeholder Fisher parses, matching QueueSqlCommand.
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

        if (store is not SqliteMessageStore database || !store.Deduplication.Enabled)
        {
            // Same failure, and the same reason for it, as MessageDeduplicator.TryClaimAsync: letting this
            // through would report the application as protected while every duplicate ran, with no log
            // line and no failing test to distinguish it from a working configuration.
            throw new InvalidOperationException(
                $"Logical message deduplication is enabled, but the message store backing this chain does not implement it. See {nameof(IDeduplicationStore)} for the providers that do. GH-4180");
        }

        // Fisher sets SchemaNameIsTablePrefix, so this is one prefixed identifier rather than
        // schema.table. DeduplicationFullName renders that through TablePrefixing; hand-built SQL must
        // not assume a qualified shape.
        return database.DeduplicationFullName;
    }
}

/// <summary>
/// GH-4571. Classification of the one failure the optimistic check cannot prevent: two genuinely
/// concurrent callers both read "not claimed", both enlist the INSERT, and the second one to commit trips
/// the deduplication table's primary key.
/// </summary>
public static class FisherDeduplicationFailures
{
    /// <summary>
    /// <see cref="IsDuplicateDeduplicationClaim" /> as a <see cref="MethodInfo" />, for
    /// <see cref="RefuseDuplicateClaimAtCommitFrame" /> to render into an exception filter.
    /// </summary>
    public static MethodInfo Classifier { get; } =
        typeof(FisherDeduplicationFailures).GetMethod(nameof(IsDuplicateDeduplicationClaim))!;

    /// <summary>
    /// Was <paramref name="exception" /> a commit that lost the race for a logical deduplication id?
    ///
    /// <para>
    /// A public pass-through to the SQLite reading of it, which lives beside the message store's other
    /// constraint classifiers and the table-name parsing they share. Public because
    /// <see cref="RefuseDuplicateClaimAtCommitFrame" /> renders the declaring type into generated source,
    /// and <c>SqliteMessageStore</c> is internal — generated code is a consumer outside the assembly, so
    /// <c>InternalsVisibleTo</c> does not reach it.
    /// </para>
    /// </summary>
    public static bool IsDuplicateDeduplicationClaim(Exception exception)
    {
        return SqliteMessageStore.IsDuplicateDeduplicationClaim(exception);
    }
}
