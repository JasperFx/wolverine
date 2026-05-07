using System.Data.Common;
using Weasel.Core;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.DynamicListeners;

/// <summary>
/// Cross-provider <see cref="IListenerStore"/> backed by a single <c>wolverine_listeners</c>
/// table. The table has a single <c>uri</c> column that is also its primary key, so
/// register/remove are a plain <c>INSERT</c> / <c>DELETE</c> by URI string with no
/// per-provider UPSERT or MERGE syntax.
///
/// Idempotent registration uses a <i>try-insert / swallow-unique-violation</i> pattern:
/// the store calls <c>INSERT</c> unconditionally and, if the database raises a
/// duplicate-key error, the supplied <see cref="Func{Exception, Boolean}"/> classifier
/// (delegated to <see cref="MessageDatabase{T}"/>'s existing
/// <c>isExceptionFromDuplicateEnvelope</c> override) recognises the violation and the
/// store returns successfully. Any other database failure bubbles up.
///
/// All operations honour the supplied cancellation token. The store does no buffering
/// — every call is a fresh round-trip — which is appropriate given the registry sees
/// register/remove operations only when an operator explicitly adds or removes a
/// listener at runtime, not on the steady-state message hot path.
/// </summary>
internal sealed class RdbmsListenerStore : IListenerStore
{
    private readonly DbDataSource _dataSource;
    private readonly Func<Exception, bool> _isUniqueConstraintViolation;
    private readonly string _insertSql;
    private readonly string _deleteSql;
    private readonly string _selectAllSql;

    public RdbmsListenerStore(
        DbDataSource dataSource,
        string quotedSchemaName,
        Func<Exception, bool> isUniqueConstraintViolation)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _isUniqueConstraintViolation = isUniqueConstraintViolation
                                       ?? throw new ArgumentNullException(nameof(isUniqueConstraintViolation));

        var table = $"{quotedSchemaName}.{DatabaseConstants.ListenersTableName}";
        _insertSql = $"insert into {table} (uri) values (@uri)";
        _deleteSql = $"delete from {table} where uri = @uri";
        _selectAllSql = $"select uri from {table}";
    }

    public async Task RegisterListenerAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));

        try
        {
            await using var cmd = _dataSource.CreateCommand(_insertSql)
                .With("uri", uri.ToString());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_isUniqueConstraintViolation(ex))
        {
            // Idempotent: the URI is already registered. The contract for
            // RegisterListenerAsync is "make this URI registered" so reaching the
            // already-registered state is success, not an error.
        }
    }

    public async Task RemoveListenerAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));

        await using var cmd = _dataSource.CreateCommand(_deleteSql)
            .With("uri", uri.ToString());

        // DELETE is naturally idempotent — removing a non-existent row affects 0 rows
        // without raising. No need to special-case "already absent" here.
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Uri>> AllListenersAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = _dataSource.CreateCommand(_selectAllSql);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<Uri>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new Uri(reader.GetString(0)));
        }

        return list;
    }
}
