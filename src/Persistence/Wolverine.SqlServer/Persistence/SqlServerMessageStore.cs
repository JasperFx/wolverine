using System.Data;
using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine.Logging;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Wolverine.SqlServer.Schema;
using Wolverine.SqlServer.Util;
using Wolverine.Transports;
using CommandExtensions = Weasel.Core.CommandExtensions;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.SqlServer.Persistence;

public class SqlServerMessageStore : MessageDatabase<SqlConnection>
{
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _moveToDeadLetterStorageSql;


    public SqlServerMessageStore(DatabaseSettings database, DurabilitySettings settings,
        ILogger<SqlServerMessageStore> logger)
        : base(database, settings, logger, new SqlServerMigrator(), "dbo")
    {
        _findAtLargeEnvelopesSql =
            $"select top (@limit) {DatabaseConstants.IncomingFields} from {database.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = @address";

        _moveToDeadLetterStorageSql = $"EXEC {SchemaName}.uspDeleteIncomingEnvelopes @IDLIST;";
    }

    protected override INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings)
    {
        return new SqlServerNodePersistence(databaseSettings);
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        return ex is SqlException sqlEx && sqlEx.Message.ContainsIgnoreCase("Violation of PRIMARY KEY constraint");
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using var conn = CreateConnection();
        await conn.OpenAsync();

        await using (var reader = await CommandExtensions.CreateCommand(conn, $"select status, count(*) from {SchemaName}.{DatabaseConstants.IncomingTable} group by status")
                         .ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0));
                var count = await reader.GetFieldValueAsync<int>(1);

                switch (status)
                {
                    case EnvelopeStatus.Incoming:
                        counts.Incoming = count;
                        break;

                    case EnvelopeStatus.Handled:
                        counts.Handled = count;
                        break;

                    case EnvelopeStatus.Scheduled:
                        counts.Scheduled = count;
                        break;
                }
            }
        }

        counts.Outgoing = (int)(await CommandExtensions.CreateCommand(conn, $"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync())!;

        counts.DeadLetter = (int)(await CommandExtensions.CreateCommand(conn, $"select count(*) from {SchemaName}.{DatabaseConstants.DeadLetterTable}")
            .ExecuteScalarAsync())!;


        await conn.CloseAsync();

        return counts;
    }
    
    /// <summary>
    ///     The value of the 'database_principal' parameter in calls to APPLOCK_TEST
    /// </summary>
    public string DatabasePrincipal { get; set; } = "dbo";

    public override Task DeleteIncomingEnvelopesAsync(Envelope[] envelopes)
    {
        return CallFunction("uspDeleteIncomingEnvelopes")
            .WithIdList(this, envelopes).ExecuteOnce(_cancellation);
    }

    public override Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        var table = new DataTable();
        table.Columns.Add(new DataColumn("ID", typeof(Guid)));
        table.Rows.Add(envelope.Id);

        var builder = ToCommandBuilder();

        var list = builder.AddNamedParameter("IDLIST", table).As<SqlParameter>();
        list.SqlDbType = SqlDbType.Structured;
        list.TypeName = $"{SchemaName}.EnvelopeIdList";

        builder.Append(_moveToDeadLetterStorageSql);

        DatabasePersistence.ConfigureDeadLetterCommands(envelope, exception, builder, this);

        return builder.Compile().ExecuteOnce(_cancellation);
    }

    public override void Describe(TextWriter writer)
    {
        writer.WriteLine($"Sql Server Envelope Storage in Schema '{SchemaName}'");
    }

    protected override string determineOutgoingEnvelopeSql(DurabilitySettings settings)
    {
        return
            $"select top {settings.RecoveryBatchSize} {DatabaseConstants.OutgoingFields} from {SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = {TransportConstants.AnyNode} and destination = @destination";
    }

    public override Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing)
    {
        var cmd = Session.CallFunction("uspMarkOutgoingOwnership")
            .WithIdList(this, outgoing)
            .With("owner", ownerId);

        return cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public override Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        var cmd = CallFunction("uspDiscardAndReassignOutgoing")
            .WithIdList(this, discards, "discards")
            .WithIdList(this, reassigned, "reassigned")
            .With("ownerId", nodeId);

        return cmd.ExecuteOnce(_cancellation);
    }

    public override Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        return CallFunction("uspDeleteOutgoingEnvelopes")
            .WithIdList(this, envelopes).ExecuteOnce(_cancellation);
    }

    public override Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        return Session.CreateCommand(_findAtLargeEnvelopesSql)
            .With("address", listenerAddress.ToString())
            .With("limit", limit)
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r));
    }

    public override Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        return Session.CallFunction("uspMarkIncomingOwnership")
            .WithIdList(this, incoming)
            .With("owner", ownerId)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public override Task<IReadOnlyList<Envelope>> LoadScheduledToExecuteAsync(DateTimeOffset utcNow)
    {
        return Session!.Transaction!
            .CreateCommand(
                $"select TOP {Durability.RecoveryBatchSize} {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= @time")
            .With("time", utcNow)
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r, _cancellation), _cancellation);
    }


    public override void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append( $"select TOP {Durability.RecoveryBatchSize} {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= @");
        builder.AppendParameter(utcNow);
        builder.Append(";");
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

    public override IEnumerable<ISchemaObject> AllObjects()
    {
        yield return new OutgoingEnvelopeTable(SchemaName);
        yield return new IncomingEnvelopeTable(SchemaName);
        yield return new DeadLettersTable(SchemaName);
        yield return new EnvelopeIdTable(SchemaName);
        yield return new WolverineStoredProcedure("uspDeleteIncomingEnvelopes.sql", this);
        yield return new WolverineStoredProcedure("uspDeleteOutgoingEnvelopes.sql", this);
        yield return new WolverineStoredProcedure("uspDiscardAndReassignOutgoing.sql", this);
        yield return new WolverineStoredProcedure("uspMarkIncomingOwnership.sql", this);
        yield return new WolverineStoredProcedure("uspMarkOutgoingOwnership.sql", this);
        
        if (_settings.IsMaster)
        {
            var nodeTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeTableName));
            nodeTable.AddColumn<Guid>("id").AsPrimaryKey();
            nodeTable.AddColumn<int>("node_number").AutoNumber().NotNull();
            nodeTable.AddColumn<string>("description").NotNull();
            nodeTable.AddColumn<string>("uri").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("GETUTCDATE()").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("health_check").DefaultValueByExpression("GETUTCDATE()").NotNull();
            nodeTable.AddColumn("capabilities", "nvarchar(max)").AllowNulls();

            yield return nodeTable;

            var assignmentTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeAssignmentsTableName));
            assignmentTable.AddColumn<string>("id").AsPrimaryKey();
            assignmentTable.AddColumn<Guid>("node_id").ForeignKeyTo(nodeTable.Identifier, "id", onDelete:CascadeAction.Cascade);
            assignmentTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("GETUTCDATE()").NotNull();

            yield return assignmentTable;
            
            if (_settings.CommandQueuesEnabled)
            {
                var queueTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.ControlQueueTableName));
                queueTable.AddColumn<Guid>("id").AsPrimaryKey();
                queueTable.AddColumn<string>("message_type").NotNull();
                queueTable.AddColumn<Guid>("node_id").NotNull();
                queueTable.AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();
                queueTable.AddColumn<DateTimeOffset>("posted").NotNull().DefaultValueByExpression("GETUTCDATE()");
                queueTable.AddColumn<DateTimeOffset>("expires");

                yield return queueTable;
            }
        }
    }
}