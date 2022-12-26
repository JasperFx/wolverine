using System.Data;
using System.Data.Common;
using Weasel.Core;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public abstract class DatabaseSettings
{
    private string _schemaName;

    protected DatabaseSettings(string defaultSchema, Migrator migrator)
    {
        _schemaName = defaultSchema;
        Migrator = migrator;

        IncomingFullName = $"{SchemaName}.{DatabaseConstants.IncomingTable}";
        OutgoingFullName = $"{SchemaName}.{DatabaseConstants.OutgoingTable}";
    }

    public string? ConnectionString { get; set; }

    public string SchemaName
    {
        get => _schemaName;
        set
        {
            _schemaName = value;

            IncomingFullName = $"{value}.{DatabaseConstants.IncomingTable}";
            OutgoingFullName = $"{value}.{DatabaseConstants.OutgoingTable}";
        }
    }

    public Migrator Migrator { get; }


    public string OutgoingFullName { get; private set; }

    public string IncomingFullName { get; private set; }

    public abstract DbConnection CreateConnection();

    public DbCommand CreateCommand(string command)
    {
        var cmd = CreateConnection().CreateCommand();
        cmd.CommandText = command;

        return cmd;
    }

    public DbCommand CallFunction(string functionName)
    {
        var cmd = CreateConnection().CreateCommand();
        cmd.CommandText = SchemaName + "." + functionName;

        cmd.CommandType = CommandType.StoredProcedure;

        return cmd;
    }

    public DbCommandBuilder ToCommandBuilder()
    {
        return CreateConnection().ToCommandBuilder();
    }


    public abstract Task GetGlobalTxLockAsync(DbConnection conn, DbTransaction tx, int lockId,
        CancellationToken cancellation = default);

    public abstract Task<bool> TryGetGlobalTxLockAsync(DbConnection conn, DbTransaction tx, int lockId,
        CancellationToken cancellation = default);

    public abstract Task GetGlobalLockAsync(DbConnection conn, int lockId, CancellationToken cancellation = default,
        DbTransaction? transaction = null);

    public abstract Task<bool> TryGetGlobalLockAsync(DbConnection conn, DbTransaction? tx, int lockId,
        CancellationToken cancellation = default);

    public abstract Task<bool> TryGetGlobalLockAsync(DbConnection conn, int lockId, DbTransaction tx,
        CancellationToken cancellation = default);

    public abstract Task ReleaseGlobalLockAsync(DbConnection conn, int lockId, CancellationToken cancellation = default,
        DbTransaction? tx = null);
}