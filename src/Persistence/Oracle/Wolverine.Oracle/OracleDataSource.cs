using System.Data.Common;
using Oracle.ManagedDataAccess.Client;

namespace Wolverine.Oracle;

/// <summary>
/// Thin DbDataSource wrapper for Oracle since Oracle.ManagedDataAccess
/// does not provide a native DbDataSource implementation.
/// </summary>
internal class OracleDataSource : DbDataSource
{
    private readonly string _connectionString;

    public OracleDataSource(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public override string ConnectionString => _connectionString;

    protected override DbConnection CreateDbConnection()
    {
        return new OracleConnection(_connectionString);
    }

    public new OracleConnection CreateConnection()
    {
        return new OracleConnection(_connectionString);
    }

    public new async Task<OracleConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = new OracleConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}
