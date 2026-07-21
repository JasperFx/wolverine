using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Postgresql;

/// <summary>
/// PostgreSQL database-LOB backed <see cref="IClaimCheckStore"/>. Each claim check payload is
/// stored as a single <c>bytea</c> row in a Wolverine-managed table in the application's own
/// PostgreSQL database — the zero-new-infrastructure option for critter-stack users, requiring no
/// S3 / Azure / GCS account. The <see cref="ClaimCheckToken.Id"/> maps to the row's primary key.
/// </summary>
public class PostgresqlClaimCheckStore : IClaimCheckStore
{
    // Postgres identifiers we build DDL/DML for are validated against this so the schema/table
    // names (which come from configuration) can be safely embedded in quoted identifiers.
    private static readonly Regex _identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schemaName;
    private readonly string _tableName;
    private readonly string _qualifiedTable;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _provisioned;

    /// <summary>The default schema used when none is supplied.</summary>
    public const string DefaultSchemaName = "public";

    /// <summary>The default table name used when none is supplied.</summary>
    public const string DefaultTableName = "wolverine_claim_check";

    /// <summary>
    /// Create a new claim check store backed by a PostgreSQL <c>bytea</c> table.
    /// </summary>
    /// <param name="dataSource">Data source for the target PostgreSQL database.</param>
    /// <param name="schemaName">Schema that owns the claim check table. Created on first use if missing.</param>
    /// <param name="tableName">Name of the claim check table. Created on first use if missing.</param>
    public PostgresqlClaimCheckStore(
        NpgsqlDataSource dataSource,
        string schemaName = DefaultSchemaName,
        string tableName = DefaultTableName)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        if (string.IsNullOrWhiteSpace(schemaName) || !_identifier.IsMatch(schemaName))
        {
            throw new ArgumentException(
                "Schema name must be a simple PostgreSQL identifier (letters, digits, underscores; not starting with a digit)",
                nameof(schemaName));
        }

        if (string.IsNullOrWhiteSpace(tableName) || !_identifier.IsMatch(tableName))
        {
            throw new ArgumentException(
                "Table name must be a simple PostgreSQL identifier (letters, digits, underscores; not starting with a digit)",
                nameof(tableName));
        }

        _schemaName = schemaName;
        _tableName = tableName;
        _qualifiedTable = $"\"{schemaName}\".\"{tableName}\"";
    }

    /// <summary>The schema that owns the claim check table.</summary>
    public string SchemaName => _schemaName;

    /// <summary>The claim check table name.</summary>
    public string TableName => _tableName;

    public async Task<ClaimCheckToken> StoreAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            throw new ArgumentException("contentType must be provided", nameof(contentType));
        }

        await ensureProvisionedAsync(cancellationToken).ConfigureAwait(false);

        var id = Guid.NewGuid().ToString("N");

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"insert into {_qualifiedTable} (id, content_type, body, length) values (@id, @ct, @body, @len)";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("ct", contentType);
        cmd.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Bytea) { Value = payload.ToArray() });
        cmd.Parameters.AddWithValue("len", (long)payload.Length);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new ClaimCheckToken(id, contentType, payload.Length);
    }

    public async Task<ReadOnlyMemory<byte>> LoadAsync(
        ClaimCheckToken token,
        CancellationToken cancellationToken = default)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        await ensureProvisionedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select body from {_qualifiedTable} where id = @id";
        cmd.Parameters.AddWithValue("id", token.Id);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
        {
            throw new KeyNotFoundException(
                $"No claim check payload found in {_qualifiedTable} for token id '{token.Id}'.");
        }

        return (byte[])result;
    }

    public async Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        await ensureProvisionedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        // Deleting a missing row is a no-op, so DeleteAsync is naturally idempotent.
        cmd.CommandText = $"delete from {_qualifiedTable} where id = @id";
        cmd.Parameters.AddWithValue("id", token.Id);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ensureProvisionedAsync(CancellationToken cancellationToken)
    {
        if (_provisioned)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_provisioned)
            {
                return;
            }

            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"create schema if not exists \"{_schemaName}\";" +
                $"create table if not exists {_qualifiedTable} (" +
                "id text primary key, " +
                "content_type text not null, " +
                "body bytea not null, " +
                "length bigint not null, " +
                "created timestamptz not null default (now() at time zone 'utc'));";

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _provisioned = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
