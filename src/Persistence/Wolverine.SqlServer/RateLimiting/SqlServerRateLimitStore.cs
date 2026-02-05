using System.Data;
using Microsoft.Data.SqlClient;
using Weasel.Core;
using Wolverine.RateLimiting;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Schema;

namespace Wolverine.SqlServer.RateLimiting;

public sealed class SqlServerRateLimitOptions
{
    public string? SchemaName { get; set; }
    public string TableName { get; set; } = "wolverine_rate_limits";
}

public sealed class SqlServerRateLimitStore : IRateLimitStore
{
    private readonly string _connectionString;
    private readonly string _qualifiedTable;
    private readonly SqlServerRateLimitOptions _options;

    public SqlServerRateLimitStore(DatabaseSettings settings, SqlServerRateLimitOptions options)
    {
        _options = options;
        _connectionString = settings.ConnectionString ?? throw new InvalidOperationException("Connection string is required.");

        var schemaName = _options.SchemaName ?? settings.SchemaName ?? "dbo";
        _qualifiedTable = new DbObjectName(schemaName, _options.TableName).QualifiedName;
    }

    public async ValueTask<RateLimitStoreResult> TryAcquireAsync(RateLimitStoreRequest request,
        CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
MERGE {_qualifiedTable} WITH (HOLDLOCK) AS target
USING (SELECT @key AS {RateLimitTableColumns.Key}, @windowStart AS {RateLimitTableColumns.WindowStart}) AS source
ON target.{RateLimitTableColumns.Key} = source.{RateLimitTableColumns.Key}
    AND target.{RateLimitTableColumns.WindowStart} = source.{RateLimitTableColumns.WindowStart}
WHEN MATCHED THEN
    UPDATE SET {RateLimitTableColumns.CurrentCount} = target.{RateLimitTableColumns.CurrentCount} + @quantity,
               {RateLimitTableColumns.WindowEnd} = @windowEnd,
               {RateLimitTableColumns.Limit} = @limitPerWindow
WHEN NOT MATCHED THEN
    INSERT ({RateLimitTableColumns.Key}, {RateLimitTableColumns.WindowStart}, {RateLimitTableColumns.WindowEnd}, {RateLimitTableColumns.Limit}, {RateLimitTableColumns.CurrentCount})
    VALUES (@key, @windowStart, @windowEnd, @limitPerWindow, @quantity)
OUTPUT inserted.{RateLimitTableColumns.CurrentCount};
";

        cmd.Parameters.Add(new SqlParameter("@key", SqlDbType.VarChar, 500) { Value = request.Key });
        cmd.Parameters.Add(new SqlParameter("@windowStart", SqlDbType.DateTimeOffset) { Value = request.Bucket.WindowStart });
        cmd.Parameters.Add(new SqlParameter("@windowEnd", SqlDbType.DateTimeOffset) { Value = request.Bucket.WindowEnd });
        cmd.Parameters.Add(new SqlParameter("@limitPerWindow", SqlDbType.Int) { Value = request.Bucket.Limit });
        cmd.Parameters.Add(new SqlParameter("@quantity", SqlDbType.Int) { Value = request.Quantity });

        var current = (int)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var allowed = current <= request.Bucket.Limit;

        return new RateLimitStoreResult(allowed, current);
    }
}
