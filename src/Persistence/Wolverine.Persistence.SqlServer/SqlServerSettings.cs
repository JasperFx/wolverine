using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Wolverine.Persistence.Database;
using Microsoft.Data.SqlClient;
using Weasel.Core;
using Weasel.SqlServer;

namespace Wolverine.Persistence.SqlServer;

public class SqlServerSettings : DatabaseSettings
{
    public SqlServerSettings() : base("dbo", new SqlServerMigrator())
    {
    }


    /// <summary>
    ///     The value of the 'database_principal' parameter in calls to APPLOCK_TEST
    /// </summary>
    public string DatabasePrincipal { get; set; } = "dbo";

    public override DbConnection CreateConnection()
    {
        return new SqlConnection(ConnectionString);
    }

    public override Task GetGlobalTxLockAsync(DbConnection conn, DbTransaction tx, int lockId,
        CancellationToken cancellation = default)
    {
        return getLockAsync(conn, lockId, "Transaction", tx, cancellation);
    }

    private static async Task getLockAsync(DbConnection conn, int lockId, string owner, DbTransaction? tx,
        CancellationToken cancellation)
    {
        var returnValue = await tryGetLockAsync(conn, lockId, owner, tx, cancellation);

        if (returnValue < 0)
        {
            throw new Exception($"sp_getapplock failed with errorCode '{returnValue}'");
        }
    }

    private static async Task<int> tryGetLockAsync(DbConnection conn, int lockId, string owner, DbTransaction? tx,
        CancellationToken cancellation)
    {
        var cmd = conn.CreateCommand("sp_getapplock");
        cmd.Transaction = tx;

        cmd.CommandType = CommandType.StoredProcedure;
        cmd.With("Resource", lockId.ToString());
        cmd.With("LockMode", "Exclusive");

        cmd.With("LockOwner", owner);
        cmd.With("LockTimeout", 1000);

        var returnValue = cmd.CreateParameter();
        returnValue.ParameterName = "ReturnValue";
        returnValue.DbType = DbType.Int32;
        returnValue.Direction = ParameterDirection.ReturnValue;
        cmd.Parameters.Add(returnValue);

        await cmd.ExecuteNonQueryAsync(cancellation);

        return (int)returnValue.Value!;
    }

    public override async Task<bool> TryGetGlobalTxLockAsync(DbConnection conn, DbTransaction tx, int lockId,
        CancellationToken cancellation = default)
    {
        return await tryGetLockAsync(conn, lockId, "Transaction", tx, cancellation) >= 0;
    }


    public override Task GetGlobalLockAsync(DbConnection conn, int lockId, CancellationToken cancellation = default,
        DbTransaction? transaction = null)
    {
        return getLockAsync(conn, lockId, "Session", transaction, cancellation);
    }

    public override async Task<bool> TryGetGlobalLockAsync(DbConnection conn, DbTransaction? tx, int lockId,
        CancellationToken cancellation = default)
    {
        return await tryGetLockAsync(conn, lockId, "Session", tx, cancellation) >= 0;
    }

    public override async Task<bool> TryGetGlobalLockAsync(DbConnection conn, int lockId, DbTransaction tx,
        CancellationToken cancellation = default)
    {
        return await tryGetLockAsync(conn, lockId, "Session", tx, cancellation) >= 0;
    }

    public override Task ReleaseGlobalLockAsync(DbConnection conn, int lockId,
        CancellationToken cancellation = default,
        DbTransaction? tx = null)
    {
        var sqlCommand = conn.CreateCommand("sp_releaseapplock");
        sqlCommand.Transaction = tx;
        sqlCommand.CommandType = CommandType.StoredProcedure;

        sqlCommand.With("Resource", lockId.ToString());
        sqlCommand.With("LockOwner", "Session");

        return sqlCommand.ExecuteNonQueryAsync(cancellation);
    }
}
