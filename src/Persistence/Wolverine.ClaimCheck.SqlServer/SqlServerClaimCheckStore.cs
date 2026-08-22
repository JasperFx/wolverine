using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.SqlServer;

/// <summary>
/// SQL Server database-LOB backed <see cref="IClaimCheckStore"/>. Each claim check payload is stored as
/// a single <c>varbinary(max)</c> row in a Wolverine-managed table in the application's own SQL Server
/// database — the zero-new-infrastructure option, requiring no S3 / Azure / GCS account. The
/// <see cref="ClaimCheckToken.Id"/> maps to the row's primary key. The sibling of
/// <c>PostgresqlClaimCheckStore</c>.
/// </summary>
public class SqlServerClaimCheckStore : IClaimCheckStoreWithExpiration
{
    // Identifiers we build DDL/DML for are validated against this so the schema/table names (which come
    // from configuration) can be safely embedded in bracket-quoted identifiers and in the dynamic SQL
    // that CREATE SCHEMA requires.
    private static readonly Regex _identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly Func<CancellationToken, Task<SqlConnection>> _connectionSource;
    private readonly string _schemaName;
    private readonly string _tableName;
    private readonly string _qualifiedTable;
    private readonly string _createdIndexName;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _provisioned;

    /// <summary>The default schema used when none is supplied.</summary>
    public const string DefaultSchemaName = "dbo";

    /// <summary>The default table name used when none is supplied.</summary>
    public const string DefaultTableName = "wolverine_claim_check";

    /// <summary>
    /// Create a new claim check store backed by a SQL Server <c>varbinary(max)</c> table.
    /// </summary>
    /// <param name="connectionString">Connection string for the target SQL Server database.</param>
    /// <param name="schemaName">Schema that owns the claim check table. Created on first use if missing.</param>
    /// <param name="tableName">Name of the claim check table. Created on first use if missing.</param>
    public SqlServerClaimCheckStore(
        string connectionString,
        string schemaName = DefaultSchemaName,
        string tableName = DefaultTableName)
        : this(buildConnectionSource(connectionString), schemaName, tableName)
    {
    }

    /// <summary>
    /// Create a new claim check store that opens its connections through <paramref name="connectionSource"/>.
    /// Use this overload when connections need custom construction, for example an access-token credential.
    /// </summary>
    /// <param name="connectionSource">Opens a connection to the target SQL Server database.</param>
    /// <param name="schemaName">Schema that owns the claim check table. Created on first use if missing.</param>
    /// <param name="tableName">Name of the claim check table. Created on first use if missing.</param>
    public SqlServerClaimCheckStore(
        Func<CancellationToken, Task<SqlConnection>> connectionSource,
        string schemaName = DefaultSchemaName,
        string tableName = DefaultTableName)
    {
        _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));

        assertIdentifier(schemaName, nameof(schemaName), "Schema");
        assertIdentifier(tableName, nameof(tableName), "Table");

        _schemaName = schemaName;
        _tableName = tableName;
        _qualifiedTable = $"[{schemaName}].[{tableName}]";
        _createdIndexName = $"[{tableName}_created_idx]";
    }

    /// <summary>The schema that owns the claim check table.</summary>
    public string SchemaName => _schemaName;

    /// <summary>The claim check table name.</summary>
    public string TableName => _tableName;

    /// <summary>
    /// Reject anything that is not a single simple identifier. A dotted value such as
    /// <c>crm.sales</c> is specifically called out: SQL Server would silently treat it as a multi-part
    /// name and the resulting DDL would not mean what the caller intended (GH-3997).
    /// </summary>
    private static void assertIdentifier(string value, string parameterName, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !_identifier.IsMatch(value))
        {
            var hint = value?.Contains('.') == true
                ? $" '{value}' looks like a multi-part name; supply only the {label.ToLowerInvariant()} itself."
                : string.Empty;

            throw new ArgumentException(
                $"{label} name must be a simple SQL Server identifier (letters, digits, underscores; not starting with a digit).{hint}",
                parameterName);
        }
    }

    private static Func<CancellationToken, Task<SqlConnection>> buildConnectionSource(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string must be provided", nameof(connectionString));
        }

        return async token =>
        {
            var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            return conn;
        };
    }

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

        await using var conn = await _connectionSource(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"insert into {_qualifiedTable} (id, content_type, body, length, created) " +
            "values (@id, @ct, @body, @len, @created)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@ct", contentType);
        cmd.Parameters.Add(new SqlParameter("@body", SqlDbType.VarBinary, -1) { Value = payload.ToArray() });
        cmd.Parameters.AddWithValue("@len", (long)payload.Length);
        // created is always written explicitly as UTC so the expiration sweep's UTC cutoff is comparable
        // no matter what time zone the server is running in.
        cmd.Parameters.Add(new SqlParameter("@created", SqlDbType.DateTime2) { Value = DateTime.UtcNow });

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new ClaimCheckToken(id, contentType, payload.Length);
    }

    public async Task<ReadOnlyMemory<byte>> LoadAsync(
        ClaimCheckToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        await ensureProvisionedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await _connectionSource(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select body from {_qualifiedTable} where id = @id";
        cmd.Parameters.AddWithValue("@id", token.Id);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            throw new KeyNotFoundException(
                $"No claim check payload found in {_qualifiedTable} for token id '{token.Id}'.");
        }

        return (byte[])result;
    }

    public async Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        await ensureProvisionedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await _connectionSource(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        // Deleting a missing row is a no-op, so DeleteAsync is naturally idempotent.
        cmd.CommandText = $"delete from {_qualifiedTable} where id = @id";
        cmd.Parameters.AddWithValue("@id", token.Id);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// GH-3509 sweep support. SQL Server supports <c>delete top (n)</c> directly, so unlike the PostgreSQL
    /// store this needs no <c>ctid</c> sub-select to bound the batch.
    /// </summary>
    public async Task<int> DeleteExpiredPayloadsAsync(
        DateTimeOffset cutoff,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
        {
            return 0;
        }

        await ensureProvisionedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await _connectionSource(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"delete top (@max) from {_qualifiedTable} where created < @cutoff";
        cmd.Parameters.AddWithValue("@max", maxCount);
        cmd.Parameters.Add(new SqlParameter("@cutoff", SqlDbType.DateTime2) { Value = cutoff.UtcDateTime });

        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

            await using var conn = await _connectionSource(cancellationToken).ConfigureAwait(false);

            // SQL Server rejects CREATE SCHEMA unless it is the FIRST statement in its batch, so this
            // cannot be concatenated with the table DDL the way the PostgreSQL store does it. Wrapping it
            // in EXEC gives it a batch of its own. The schema name is identifier-validated in the
            // constructor, so embedding it in the dynamic SQL is safe.
            await using (var schema = conn.CreateCommand())
            {
                schema.CommandText =
                    "if not exists (select 1 from sys.schemas where name = @schema) " +
                    $"exec('create schema [{_schemaName}]');";
                schema.Parameters.AddWithValue("@schema", _schemaName);
                await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var table = conn.CreateCommand())
            {
                table.CommandText =
                    $"if object_id('{_schemaName}.{_tableName}', 'U') is null " +
                    $"create table {_qualifiedTable} (" +
                    "id nvarchar(100) not null primary key, " +
                    "content_type nvarchar(255) not null, " +
                    "body varbinary(max) not null, " +
                    "length bigint not null, " +
                    "created datetime2 not null constraint " +
                    $"[DF_{_tableName}_created] default (sysutcdatetime()));";
                await table.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var index = conn.CreateCommand())
            {
                // GH-3509: the expiration sweep filters on created, and every row here is a large
                // payload -- without this index the sweep scans the whole table on every pass.
                index.CommandText =
                    $"if not exists (select 1 from sys.indexes where name = '{_tableName}_created_idx' " +
                    $"and object_id = object_id('{_schemaName}.{_tableName}')) " +
                    $"create index {_createdIndexName} on {_qualifiedTable} (created);";
                await index.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _provisioned = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
