using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Weasel.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace Wolverine.Oracle;

/// <summary>
/// Oracle-specific <see cref="IListenerStore"/>. Mirrors the
/// <c>Wolverine.RDBMS.DynamicListeners.RdbmsListenerStore</c> contract but uses
/// Oracle's <c>:</c>-prefixed bind variables and the per-call
/// <c>OpenConnectionAsync + conn.CreateCommand</c> idiom that the rest of
/// Wolverine.Oracle follows (see <see cref="OracleNodePersistence"/>) — the shared
/// <see cref="System.Data.Common.DbDataSource"/>-driven path used by Postgres /
/// SqlServer / MySQL / SQLite isn't a clean fit because Oracle's managed driver
/// doesn't ship a native <c>DbDataSource</c> implementation and Wolverine.Oracle
/// uses its own thin <see cref="OracleDataSource"/> wrapper.
///
/// Idempotent registration uses the same try-insert / swallow-unique-violation
/// pattern as <c>RdbmsListenerStore</c>: an <c>INSERT</c> is attempted
/// unconditionally and ORA-00001 (unique constraint violated) is recognised as
/// success rather than failure.
/// </summary>
internal sealed class OracleListenerStore : IListenerStore
{
    private readonly OracleDataSource _dataSource;
    private readonly string _insertSql;
    private readonly string _deleteSql;
    private readonly string _selectAllSql;

    public OracleListenerStore(OracleDataSource dataSource, string schemaName)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        var qualifiedSchema = schemaName.ToUpperInvariant();
        var table = $"{qualifiedSchema}.{DatabaseConstants.ListenersTableName.ToUpperInvariant()}";

        _insertSql = $"INSERT INTO {table} (uri) VALUES (:uri)";
        _deleteSql = $"DELETE FROM {table} WHERE uri = :uri";
        _selectAllSql = $"SELECT uri FROM {table}";
    }

    public async Task RegisterListenerAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand(_insertSql);
        cmd.With("uri", uri.ToString());

        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException e) when (e.Number == 1) // ORA-00001: unique constraint violated
        {
            // Idempotent: the URI is already registered. Reaching the
            // already-registered state is success, not an error.
        }

        await conn.CloseAsync().ConfigureAwait(false);
    }

    public async Task RemoveListenerAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand(_deleteSql);
        cmd.With("uri", uri.ToString());

        // DELETE is naturally idempotent — removing a non-existent row affects 0 rows
        // without raising. No special-casing needed for "already absent".
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await conn.CloseAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Uri>> AllListenersAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand(_selectAllSql);

        var list = new List<Uri>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new Uri(reader.GetString(0)));
            }
        }

        await conn.CloseAsync().ConfigureAwait(false);
        return list;
    }
}
