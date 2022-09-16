using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.Persistence.Database;

namespace Wolverine.Persistence.Postgresql;

public class PostgresqlSettings : DatabaseSettings
{
    public PostgresqlSettings() : base("public", new PostgresqlMigrator())
    {
    }

    public override DbConnection CreateConnection()
    {
        return new NpgsqlConnection(ConnectionString);
    }

    public override Task GetGlobalTxLockAsync(DbConnection conn, DbTransaction tx, int lockId,
        CancellationToken cancellation = default)
    {
        return tx.CreateCommand("SELECT pg_advisory_xact_lock(:id);").With("id", lockId)
            .ExecuteNonQueryAsync(cancellation);
    }

    public override async Task<bool> TryGetGlobalTxLockAsync(DbConnection conn, DbTransaction tx, int lockId,
        CancellationToken cancellation = default)
    {
        var c = await tx.CreateCommand("SELECT pg_try_advisory_xact_lock(:id);")
            .With("id", lockId)
            .ExecuteScalarAsync(cancellation);

        return (bool)c!;
    }

    public override Task GetGlobalLockAsync(DbConnection conn, int lockId, CancellationToken cancellation = default,
        DbTransaction? transaction = null)
    {
        return conn.CreateCommand("SELECT pg_advisory_lock(:id);").With("id", lockId)
            .ExecuteNonQueryAsync(cancellation);
    }

    public override async Task<bool> TryGetGlobalLockAsync(DbConnection conn, DbTransaction? tx, int lockId,
        CancellationToken cancellation = default)
    {
        var c = await conn.CreateCommand("SELECT pg_try_advisory_lock(:id);")
            .With("id", lockId)
            .ExecuteScalarAsync(cancellation);

        return (bool)c!;
    }

    public override async Task<bool> TryGetGlobalLockAsync(DbConnection conn, int lockId, DbTransaction tx,
        CancellationToken cancellation = default)
    {
        var c = await conn.CreateCommand("SELECT pg_try_advisory_xact_lock(:id);")
            .With("id", lockId)
            .ExecuteScalarAsync(cancellation);

        return (bool)c!;
    }

    public override Task ReleaseGlobalLockAsync(DbConnection conn, int lockId,
        CancellationToken cancellation = default,
        DbTransaction? tx = null)
    {
        return conn.CreateCommand("SELECT pg_advisory_unlock(:id);").With("id", lockId)
            .ExecuteNonQueryAsync(cancellation);
    }
}
