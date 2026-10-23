using System.Reflection;
using ImTools;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Linq.QueryHandlers;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Postgresql;
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

    /// <summary>
    /// Queue a claim that lasts <paramref name="window" />, or
    /// <see cref="DurabilitySettings.DeduplicationWindow" /> when that is null. Otherwise the same as the
    /// overload without a window.
    /// </summary>
    /// <remarks>
    /// Optional, and defaulted to the overload without a window so that an existing implementation keeps
    /// compiling and loading. That default <b>ignores</b> <paramref name="window" /> and claims for the
    /// host-wide window, so an implementation must override this member to honour a chain's window.
    /// </remarks>
    void QueueClaim(IDocumentSession session, string deduplicationId, TimeSpan? window,
        Type? ancillaryStoreMarker)
        => QueueClaim(session, deduplicationId, ancillaryStoreMarker);
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
        return new DeduplicationClaimExistsHandler(tableFor(ancillaryStoreMarker), deduplicationId, logDuplicate,
            _runtime.Options.Durability.MessageDeduplicationMode);
    }

    public void QueueClaim(IDocumentSession session, string deduplicationId, Type? ancillaryStoreMarker)
        => QueueClaim(session, deduplicationId, null, ancillaryStoreMarker);

    public void QueueClaim(IDocumentSession session, string deduplicationId, TimeSpan? window,
        Type? ancillaryStoreMarker)
    {
        // Stored rather than computed at read time, exactly as MessageDeduplicator does, so that
        // shortening the window later cannot retroactively un-claim ids recorded under the longer one.
        var expires = DateTimeOffset.UtcNow.Add(window ?? _runtime.Options.Durability.DeduplicationWindow);

        session.QueueOperation(
            new ClaimDeduplicationId(tableFor(ancillaryStoreMarker), deduplicationId, expires,
                _runtime.Options.Durability.MessageDeduplicationMode));
    }

    private void logDuplicate(string deduplicationId)
    {
        // Information rather than Debug, on the same reasoning as MessageDeduplicator: this is a
        // business event ("that work was already done"), it is rare by construction, and a duplicate
        // that vanishes silently is indistinguishable from a message that was lost.
        _logger.LogInformation(
            "Discarding duplicate work for logical deduplication id '{DeduplicationId}'; it was already claimed within its deduplication window",
            deduplicationId);
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
/// GH-5160. The seam generated code calls to write a <c>[DeduplicatedWithResponse]</c> claim, with its response, inside
/// the Marten session's transaction. See <see cref="ITransactionalReplayableDeduplicationProvider" />.
///
/// <para>
/// A class of its own rather than a member on <see cref="IMartenDeduplicator" />, which an application may have
/// implemented, so nothing it declares changes.
/// </para>
/// </summary>
public sealed class MartenDeduplicatedResponses
{
    private readonly IWolverineRuntime _runtime;

    public MartenDeduplicatedResponses(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// Queue <paramref name="claim" /> onto <paramref name="session" />'s unit of work. Null queues nothing.
    /// Nothing is written until <c>SaveChangesAsync</c>.
    /// </summary>
    public void QueueClaim(IDocumentSession session, PendingDeduplicatedResponseClaim? claim,
        Type? ancillaryStoreMarker)
    {
        if (claim == null) return;

        session.QueueOperation(new ClaimDeduplicatedResponse(tableFor(session, ancillaryStoreMarker), claim));
    }

    private ImHashMap<(NpgsqlDataSource DataSource, Type? Marker), string> _tables =
        ImHashMap<(NpgsqlDataSource DataSource, Type? Marker), string>.Empty;

    private string tableFor(IDocumentSession session, Type? ancillaryStoreMarker)
    {
        // By store as well as data source: an application can hand one NpgsqlDataSource to its main and ancillary
        // stores, whose message stores keep their claims in different schemas
        var sessionDataSource = session.Database.As<PostgresqlDatabase>().DataSource;
        var key = (sessionDataSource, ancillaryStoreMarker);
        if (_tables.TryFind(key, out var table)) return table;

        var store = MessageStoreFor(_runtime, ancillaryStoreMarker) ?? throw new InvalidOperationException(
            $"[DeduplicatedWithResponse] needs {nameof(DurabilitySettings)}.{nameof(DurabilitySettings.EnableDeduplicatedResponses)} = true on the PostgreSQL message store this Marten store integrates with. See GH-4742");

        // Checked again here, not only when the endpoint was compiled: pre-generated code carries that decision
        // from whatever machine generated it, and a claim written to another database is never read back
        if (!IsSameDatabase(store.NpgsqlDataSource, sessionDataSource))
        {
            throw new InvalidOperationException(
                $"[DeduplicatedWithResponse(ClaimInTransaction = true)] was compiled to write its claim inside the Marten transaction, but this host's message store is in a different database from that Marten store, so the claim would never be read back. Regenerate the code against this configuration, or turn ClaimInTransaction off. See GH-5160");
        }

        table = store.DeduplicatedResponsesFullName;
        _tables = _tables.AddOrUpdate(key, table);
        return table;
    }

    /// <summary>Do the two data sources point at the same database, as far as their connection strings say?</summary>
    internal static bool IsSameDatabase(NpgsqlDataSource left, NpgsqlDataSource right)
    {
        if (ReferenceEquals(left, right)) return true;

        var a = new NpgsqlConnectionStringBuilder(left.ConnectionString);
        var b = new NpgsqlConnectionStringBuilder(right.ConnectionString);

        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port &&
               string.Equals(a.Database, b.Database, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message store a <c>[DeduplicatedWithResponse]</c> claim is read back from, on the terms
    /// <c>DeduplicatedResponses</c> uses (an ancillary store, else the main one, else the main database of a
    /// multi-tenanted store), or null when it is not a PostgreSQL store with the claims table.
    /// </summary>
    internal static PostgresqlMessageStore? MessageStoreFor(IWolverineRuntime runtime, Type? ancillaryStoreMarker)
    {
        var store = ancillaryStoreMarker != null
            ? runtime.Stores.FindAncillaryStore(ancillaryStoreMarker)
            : runtime.Storage is MultiTenantedMessageStore tenanted
                ? tenanted.Main
                : runtime.Storage;

        return store is PostgresqlMessageStore database and IReplayableDeduplicationStore { Enabled: true }
            ? database
            : null;
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
    ///
    /// <para>
    /// GH-4757: through <see cref="IncomingTableNaming.IsDeduplicationTable" /> rather than against one
    /// constant, because <c>CompareByHash</c> claims a differently named table and this classifier has
    /// no idea which mode is configured.
    /// </para>
    /// </summary>
    public static bool IsDuplicateDeduplicationClaim(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation
                                          && IncomingTableNaming.IsDeduplicationTable(pg.TableName))
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
